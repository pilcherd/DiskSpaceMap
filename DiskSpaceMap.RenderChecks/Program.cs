using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiskSpaceMap.Core;
using DiskSpaceMap.TestSupport;

namespace DiskSpaceMap.RenderChecks;

internal static class Program
{
    private static bool _saveRenders;
    // No Window.Show, Application.Run, input injection, or desktop capture.
    // Controls and an unshown application window are rendered only into memory.
    [STAThread]
    internal static void Main(string[] args)
    {
        _saveRenders = args.Contains("--render");
        try
        {
            if (args is ["--render-folder", var folder, var output]) { RenderFolder(folder, output); return; }
            Run();
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    }

    private static void RenderFolder(string folder, string output)
    {
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var result = new DiskScanner().ScanAsync(folder).GetAwaiter().GetResult();
        var application = new App(openMainWindow: false);
        application.InitializeComponent();
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var panel = new Grid { Margin = new Thickness(16) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        heading.Children.Add(new TextBlock
        {
            Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(result.Root.FullPath)),
            FontFamily = new FontFamily("Segoe UI"), FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White
        });
        heading.Children.Add(new TextBlock
        {
            Text = $"{SizeFormatter.Format(result.Root.Size)}  ·  {result.Root.FileCount:N0} files  ·  {result.Root.DirectoryCount:N0} folders",
            FontFamily = new FontFamily("Segoe UI"), FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(148, 164, 185)),
            Margin = new Thickness(0, 5, 0, 0)
        });
        panel.Children.Add(heading);
        var map = new TreemapView { Root = result.Root };
        Grid.SetRow(map, 1);
        panel.Children.Add(map);
        var hint = new TextBlock
        {
            Text = "Area = file size  ·  Click a folder to show its contents  ·  Right-click to open in Explorer",
            FontFamily = new FontFamily("Segoe UI"), FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(148, 164, 185)),
            Margin = new Thickness(0, 12, 0, 0)
        };
        Grid.SetRow(hint, 2);
        panel.Children.Add(hint);
        var surface = new Border { Background = new SolidColorBrush(Color.FromRgb(24, 31, 43)), CornerRadius = new CornerRadius(9), Child = panel };
        surface.Measure(new Size(1232, 850));
        surface.Arrange(new Rect(0, 0, 1232, 850));
        surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1232, 850, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string destination = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using (var stream = File.Create(destination)) encoder.Save(stream);
        Console.WriteLine($"Rendered {result.Root.FullPath}: {SizeFormatter.Format(result.Root.Size)}, {result.Root.Children.Count} immediate items, {result.Errors} unreadable folders. No window was opened.");
    }

    private static void Run()
    {
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        using var temporary = new TemporaryDirectory();
        string fixture = temporary.Path;
        string recyclePath = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "$Recycle.Bin");
        var nameTask = ShellDisplayNames.GetDirectoryNameAsync(recyclePath, "$Recycle.Bin");
        Require(ReferenceEquals(nameTask, ShellDisplayNames.GetDirectoryNameAsync(recyclePath, "$Recycle.Bin")), "Shell name lookup was not cached");
        Require(nameTask.Wait(TimeSpan.FromSeconds(15)), "Shell name lookup timed out");
        Require(!string.IsNullOrWhiteSpace(nameTask.Result) && nameTask.Result != "$Recycle.Bin", "Shell did not supply the localized Recycle Bin name");
        var missing = ShellDisplayNames.GetDirectoryNameAsync(Path.Combine(fixture, "missing"), "missing");
        Require(missing.Wait(TimeSpan.FromSeconds(15)) && missing.Result == "missing", "Unavailable Shell names did not fall back safely");
        Console.WriteLine($"PASS  Background Shell names, caching and fallback ({nameTask.Result})");
        foreach (string name in new[] { "00-empty", "20-empty", "40-empty", "60-empty" }) Directory.CreateDirectory(Path.Combine(fixture, name));
        Write("10-small/data.dat", 20);
        Write("30-large/data.dat", 100);
        Write("50-other/data.dat", 50);
        Write("root.dat", 7);

        var (result, first, last) = Scan(fixture);
        Require(ShellDisplayNames.Get(result.Root) == result.Root.Name, "Shell names changed a root path");
        var file = result.Root.Children.Single(node => node.Name == "root.dat");
        Require(ShellDisplayNames.Get(file) == "root.dat" && file.FullPath == Path.Combine(fixture, "root.dat"), "Shell names changed a file extension or path");
        var model = new ProgressiveMap();
        var initial = model.Update(first, false);
        var measured = model.Update(last, false);
        var completed = model.Update(result.Root, true);
        Require(initial.Select(e => e.Key).SequenceEqual(completed.Select(e => e.Key)), "Completion changed block identities");
        Require(measured.Select(e => e.Weight).SequenceEqual(completed.Select(e => e.Weight)), "Measured blocks changed size solely because the scan finished");
        Require(completed.Count(e => e.Weight == 0) == 4, "Zero-weight partition slots were removed");
        Require(completed.All(entry => ReferenceEquals(entry.Node, result.Root.Children.Single(n => n.Name == entry.Node.Name))), "Final blocks did not bind to the full drill-down tree");

        var map = CreateMap();
        map.ShowLive(first);
        map.ShowLive(last);
        var before = Capture(map, "live");
        map.Root = result.Root;
        var after = Capture(map, "complete");
        Same(before, after);
        Console.WriteLine("PASS  Actual WPF blocks keep identical bounds and colors at completion, including empty partition slots");

        map.Root = result.Root.Children.First(node => node.IsDirectory && node.Size > 0);
        Capture(map);
        map.Root = result.Root;
        Same(after, Capture(map));
        Console.WriteLine("PASS  Returning from drill-down restores the settled layout");

        Write("Drill/alpha.dat", 900);
        Write("Drill/beta.dat", 300);
        Write("Drill/gamma.dat", 100);
        Write("Drill/Nested/large.dat", 200);
        Write("Drill/Nested/small.dat", 50);
        var (drillResult, drillFirst, drillLast) = Scan(fixture);
        var drillMap = CreateMap();
        drillMap.ShowLive(drillFirst);
        drillMap.ShowLive(drillLast);
        drillMap.Root = drillResult.Root;
        var drillModel = new ProgressiveMap();
        drillModel.Update(drillFirst, false);
        var drillEntries = drillModel.Update(drillResult.Root, true);
        int drillIndex = drillEntries.ToList().FindIndex(entry => entry.Node.Name == "Drill");
        var drillBounds = TreemapLayout.LayoutOrdered(drillEntries.Select(entry => entry.Weight).ToArray(), new(0, 0, 900, 600))
            .Single(tile => tile.Index == drillIndex).Bounds;
        int invoked = 0;
        drillMap.FolderInvoked += node => { invoked++; drillMap.Root = drillResult.Root.ResolveSnapshotNode(node); };
        Require(Click(drillMap, Center(drillBounds)), "Clicking a folder did not navigate");
        var drillNode = drillResult.Root.Children.Single(node => node.Name == "Drill");
        Require(ReferenceEquals(drillMap.Root, drillNode), "Click navigated to a snapshot instead of the scanned folder");
        var firstLevel = Capture(drillMap);
        CheckChildBoxes(drillNode, firstLevel);
        Require(firstLevel.Count == 4, "First drill-down did not show all four child boxes");
        int nestedIndex = drillNode.Children.ToList().FindIndex(node => node.Name == "Nested");
        var nestedBounds = TreemapLayout.Layout(drillNode.Children.Select(node => node.Size).ToArray(), new(0, 0, 900, 600))
            .Single(tile => tile.Index == nestedIndex).Bounds;
        Require(Click(drillMap, Center(nestedBounds)), "Second-level folder click did not navigate");
        var nestedNode = drillNode.Children[nestedIndex];
        Require(ReferenceEquals(drillMap.Root, nestedNode) && invoked == 2, "Navigation skipped or repeated a level");
        var secondLevel = Capture(drillMap);
        CheckChildBoxes(nestedNode, secondLevel);
        Require(secondLevel.Count == 2, "Second drill-down did not show both file boxes");
        Require(!Click(drillMap, new Point(450, 300)) && invoked == 2, "Clicking a file navigated or opened it");
        drillMap.Root = drillNode;
        Same(firstLevel, Capture(drillMap));
        Require(!drillMap.ActivateAt(Center(nestedBounds), MouseButton.Right) && invoked == 2, "Right-click drilled into a folder instead of showing the menu");
        Console.WriteLine("PASS  Folder clicks drill through multiple levels of proportional child boxes; files stay selectable");

        var preview = drillLast.Children.Single(node => node.Name == "Drill");
        Require(preview.Children.Count == 0, "Fixture did not produce a shallow snapshot");
        Require(ReferenceEquals(drillResult.Root.ResolveSnapshotNode(preview), drillNode), "Shallow preview did not resolve to the full child tree");
        Console.WriteLine("PASS  Snapshot navigation resolves the full scanned descendants without another scan");

        var redrawMap = CreateMap();
        redrawMap.Root = drillResult.Root;
        redrawMap.SelectionChanged += _ => redrawMap.Root = drillResult.Root;
        redrawMap.FolderInvoked += node => redrawMap.Root = node;
        ClickFolder(redrawMap, "Drill");
        Require(ReferenceEquals(redrawMap.Root, drillNode), "Selection/layout rebuild discarded the clicked folder");
        CheckChildBoxes(drillNode, Capture(redrawMap));
        Console.WriteLine("PASS  Folder activation survives a map rebuild during selection and needs no mouse-release event");

        // The outer area is the current folder. No descendants may appear before
        // clicking their immediate parent, including when the initial scan finishes.
        Write("Click/Users/Profile/Documents/manual.pdf", 16000);
        Write("Click/Users/Profile/Documents/design.docx", 8000);
        Write("Click/Users/Profile/Downloads/setup.exe", 128000);
        Write("Click/Users/Profile/Media/video.mp4", 64000);
        Write("Click/Users/Profile/Media/photos.zip", 32000);
        Write("Click/Users/Profile/AppData/Local/Cache/cache-a.bin", 8000);
        Write("Click/Users/Profile/AppData/Local/Cache/cache-b.bin", 4000);
        Write("Click/Users/Public/readme.txt", 1);
        Write("Click/Users/Default/readme.txt", 1);
        var (clickResult, clickFirst, clickLast) = Scan(Path.Combine(fixture, "Click"));
        var clickMap = CreateMap();
        clickMap.ShowLive(clickFirst);
        clickMap.ShowLive(clickLast);
        var initialScope = Capture(clickMap);
        clickMap.Root = clickResult.Root;
        Same(initialScope, Capture(clickMap));
        ImmediateScope(clickMap);
        Require(clickMap.VisibleTiles.All(tile => tile.Node?.Name == "Users"), "Scan completion rendered folders inside Users before a click");
        clickMap.FolderInvoked += node => clickMap.Root = clickResult.Root.ResolveSnapshotNode(node);
        ClickFolder(clickMap, "Users");
        var users = clickResult.Root.Children.Single(node => node.Name == "Users");
        Require(ReferenceEquals(clickMap.Root, users), "Users did not become the whole map scope");
        var usersBoxes = Capture(clickMap, "click-users");
        ImmediateScope(clickMap);
        Require(!clickMap.VisibleTiles.Any(tile => tile.Node?.Name == "Documents"), "Users view pre-rendered profile contents");
        ClickFolder(clickMap, "Profile");
        var profile = users.Children.Single(node => node.Name == "Profile");
        Require(ReferenceEquals(clickMap.Root, profile), "Profile did not become the whole map scope");
        var profileBoxes = Capture(clickMap, "click-profile");
        ImmediateScope(clickMap);
        CheckChildBoxes(profile, profileBoxes);
        Require(!clickMap.VisibleTiles.Any(tile => tile.Node?.Name == "manual.pdf"), "Profile view pre-rendered Documents files");
        ClickFolder(clickMap, "Documents");
        var documents = profile.Children.Single(node => node.Name == "Documents");
        Require(ReferenceEquals(clickMap.Root, documents), "Documents did not become the whole map scope");
        ImmediateScope(clickMap);
        CheckChildBoxes(documents, Capture(clickMap, "click-documents"));
        clickMap.Root = users;
        Same(usersBoxes, Capture(clickMap));
        Console.WriteLine("PASS  Drive -> Users -> Profile -> Documents: each click replaces the outer scope with only its immediate items");

        string chain = string.Join(Path.DirectorySeparatorChar, Enumerable.Range(0, 64).Select(i => $"L{i:00}"));
        Write($"Deep/{chain}/leaf.dat", 100);
        var (deep, _, _) = Scan(Path.Combine(fixture, "Deep"));
        var deepMap = CreateMap();
        deepMap.Root = deep.Root;
        deepMap.FolderInvoked += node => deepMap.Root = deep.Root.ResolveSnapshotNode(node);
        for (int i = 0; i < 64; i++)
        {
            Capture(deepMap);
            ImmediateScope(deepMap);
            Require(deepMap.VisibleTiles.Count == 1, "Unclicked descendants were pre-rendered");
            ClickFolder(deepMap, $"L{i:00}");
            Require(deepMap.Root?.Name == $"L{i:00}", "Deep navigation skipped a level or stopped at a fixed depth");
        }
        CheckChildBoxes(deepMap.Root!, Capture(deepMap));
        Require(!Click(deepMap, new Point(450, 300)), "Leaf file unexpectedly navigated");
        Console.WriteLine("PASS  64 successive folder clicks have no depth cap and never pre-render later levels");

        string widePath = Path.Combine(fixture, "Wide");
        for (int i = 0; i < 540; i++) Write($"Wide/Folder {i:000}/data.dat", i + 1);
        var (wide, wideFirst, wideLast) = Scan(widePath);
        var wideMap = CreateMap();
        wideMap.ShowLive(wideFirst);
        wideMap.ShowLive(wideLast);
        var wideBefore = Capture(wideMap);
        wideMap.Root = wide.Root;
        Same(wideBefore, Capture(wideMap));
        var wideModel = new ProgressiveMap();
        wideModel.Update(wideFirst, false);
        var wideMeasured = wideModel.Update(wideLast, false);
        var wideCompleted = wideModel.Update(wide.Root, true);
        Require(wideCompleted.Count == 512 && wideCompleted.Select(entry => entry.Key).SequenceEqual(wideMeasured.Select(entry => entry.Key)), "Visible overflow grouping changed at completion");
        Require(wideCompleted.Single(entry => entry.Node.IsAggregate).Weight == wideMeasured.Single(entry => entry.Node.IsAggregate).Weight, "Grouped bytes changed at completion");
        Console.WriteLine("PASS  Wide-root grouping stays at the same 512 blocks after completion");

        const string tinyName = "a-very-small-folder-whose-full-name-must-be-visible";
        Write("Hints/Big/content.bin", 1000000);
        Write($"Hints/{tinyName}/content.bin", 10000);
        var (hints, _, _) = Scan(Path.Combine(fixture, "Hints"));
        var hintMap = CreateMap();
        hintMap.Root = hints.Root;
        Capture(hintMap);
        var tinyTile = hintMap.VisibleTiles.Single(tile => tile.Node?.Name == tinyName);
        Require(tinyTile.Bounds.Width < 64 || tinyTile.Bounds.Height < 34, "Fixture did not produce an unlabeled small square");
        string? hint = hintMap.HoverAt(new Point(tinyTile.Bounds.X + tinyTile.Bounds.Width / 2, tinyTile.Bounds.Y + tinyTile.Bounds.Height / 2));
        Require(hint != null && hint.Contains(tinyName) && hint.Contains(tinyTile.Node!.FullPath), "Small-square hover omitted the full name or path");
        Require(hint.Contains("Click to open this folder") && hint.Contains("Right-click"), "Folder hover has no action hint");
        Require(ReferenceEquals(hintMap.Root, hints.Root), "Hovering navigated into a folder");
        hintMap.Root = hints.Root.Children.Single(node => node.Name == "Big");
        Capture(hintMap);
        string? fileHint = hintMap.HoverAt(new Point(450, 300));
        Require(fileHint != null && fileHint.Contains("content.bin") && fileHint.Contains("show this file in Explorer"), "File hover has no name or Explorer hint");
        Require(hintMap.HoverAt(new Point(-10, -10)) == null, "Hover outside the map left a stale hint");
        Console.WriteLine("PASS  Unlabeled small squares show full names, paths, byte sizes and action hints without navigating");

        // Load actual application resources and real XAML handlers. Application
        // startup is explicitly disabled; never show this window or run its scan.
        var application = new App(openMainWindow: false);
        application.InitializeComponent();
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(application.Dispatcher));
        var window = new MainWindow(scanOnOpening: false);
        foreach (var workArea in new[] {
            new Rect(0, 0, 1920, 1040), new Rect(0, 0, 1366, 728), new Rect(0, 0, 1280, 680),
            new Rect(-1920, 40, 1920, 1040), new Rect(3840, -1080, 2560, 1400) })
        foreach (double dpi in new[] { 96d, 120d, 144d, 192d, 240d })
        {
            var placement = WindowStartupSizing.Calculate(workArea, dpi, new Size(1380, 900), new Size(1080, 720));
            WindowStartupSizing.ApplySize(window, placement);
            Require(workArea.Contains(placement.PixelBounds), "Startup bounds extend beyond the monitor work area");
            Require(window.Width * dpi / 96 <= workArea.Width && window.Height * dpi / 96 <= workArea.Height,
                "DPI-scaled startup dimensions exceed the screen");
            Require(window.MinWidth <= window.Width && window.MinHeight <= window.Height,
                "Minimum window size overrides the fit on a smaller screen");
            Require(Math.Abs(placement.PixelBounds.Left + placement.PixelBounds.Width / 2 - (workArea.Left + workArea.Width / 2)) <= 1
                && Math.Abs(placement.PixelBounds.Top + placement.PixelBounds.Height / 2 - (workArea.Top + workArea.Height / 2)) <= 1,
                "Startup window is not centered on its own monitor");
        }
        Console.WriteLine("PASS  Startup size fits the work area at 100–250% scaling, including smaller and secondary monitors");
        var surface = (FrameworkElement)window.Content;
        window.Content = null;
        window.DisplayCompletedScan(clickResult);
        Layout();
        HostScope(clickResult.Root);
        ClickFolder(window.Map, "Users");
        Layout();
        HostScope(users);
        ClickFolder(window.Map, "Profile");
        Layout();
        HostScope(profile);
        Require(window.Entries.Items.Count == 4 && window.Map.VisibleTiles.Count == 4,
            "Real window kept the selected profile block instead of its four child boxes");
        Capture(window.Map, "window-profile");
        ClickFolder(window.Map, "Documents");
        Layout();
        HostScope(documents);
        Require(window.Map.VisibleTiles.Count == 2, "Real window failed to render Documents files");
        window.UpButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout();
        HostScope(profile);
        var usersCrumb = window.Breadcrumbs.Children.OfType<Button>().Single(button => (string)button.Content == "Users");
        usersCrumb.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout();
        HostScope(users);
        window.RootButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout();
        HostScope(clickResult.Root);
        Console.WriteLine("PASS  Real window XAML wiring updates map, title, list and breadcrumbs on each press; Up/Root/breadcrumb navigation works");

        ClickFolder(window.Map, "Users");
        Layout();
        ClickFolder(window.Map, "Profile");
        Layout();
        long oldSize = clickResult.Root.Size;
        long oldFolders = clickResult.Root.DirectoryCount;
        var defaultFolder = users.Children.Single(node => node.Name == "Default");
        Write("Click/Users/Default/readme.txt", 61);
        Write("Click/Users/Profile/Added/Deep/fresh.dat", 7000);
        Write("Click/Users/Profile/Media/video.mp4", 96000);
        File.Delete(Path.Combine(fixture, "Click/Users/Profile/Media/photos.zip"));
        Write("Click/Users/Profile/AppData/Local/Cache/cache-a.bin", 12000);
        Refresh();
        var refreshedProfile = window.Map.Root!;
        HostScope(refreshedProfile);
        Require(refreshedProfile.FullPath == profile.FullPath && !ReferenceEquals(refreshedProfile, profile), "Refresh lost the current folder or reused stale contents");
        Require(refreshedProfile.Parent == users && window.Map.VisibleTiles.Count == 5, "Refresh did not replace the immediate child boxes");
        Require(refreshedProfile.Children.Single(node => node.Name == "Media").Children.Count == 1, "Deleted descendant remained in refreshed data");
        Require(clickResult.Root.Size == oldSize + 11000 && clickResult.Root.DirectoryCount == oldFolders + 2, "Refresh did not update ancestor sizes or counts");
        Require(defaultFolder.Size == 1 && users.Children.Contains(defaultFolder), "Scoped refresh read or replaced an unrelated sibling");
        Require(window.BytesStat.Text == SizeFormatter.Format(clickResult.Root.Size), "Drive totals show only the refreshed subtree");
        ClickFolder(window.Map, "Added");
        Layout();
        ClickFolder(window.Map, "Deep");
        Layout();
        Require(window.Map.Root!.Children.Single().Name == "fresh.dat", "New descendants cannot be drilled into after refresh");
        window.UpButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout();
        window.UpButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout();
        HostScope(refreshedProfile);
        Console.WriteLine("PASS  Refresh button rescans only the current subtree, preserves scope and updates ancestor totals and navigation");

        var (layoutTree, _, _) = Scan(Path.Combine(fixture, "Click"));
        var layoutFolder = layoutTree.Root.Children.Single().Children.Single(node => node.Name == "Profile");
        var (layoutScan, _, layoutLast) = Scan(layoutFolder.FullPath);
        var refreshMap = CreateMap();
        refreshMap.ShowLive(layoutLast);
        var liveRefreshBoxes = Capture(refreshMap);
        refreshMap.Root = layoutFolder.ReplaceWith(layoutScan.Root);
        Same(liveRefreshBoxes, Capture(refreshMap));
        ImmediateScope(refreshMap);
        Console.WriteLine("PASS  Scoped refresh keeps live block positions when standalone scan nodes join the drive tree");

        Write("Click/Users/Profile/Added/Deep/fresh.dat", 9000);
        Refresh(stop: true);
        Require(ReferenceEquals(window.Map.Root, refreshedProfile) && clickResult.Root.Size == oldSize + 11000, "Stopped refresh replaced the previous map");
        Require(window.StatusText.Text.Contains("previous view kept"), "Stopped refresh did not explain that the old view was retained");
        Console.WriteLine("PASS  Stopping a refresh retains the previous subtree and totals");

        ClickFolder(window.Map, "Added");
        Layout();
        ClickFolder(window.Map, "Deep");
        Layout();
        var missingFolder = window.Map.Root!;
        File.Delete(Path.Combine(fixture, "Click/Users/Profile/Added/Deep/fresh.dat"));
        Directory.Delete(Path.Combine(fixture, "Click/Users/Profile/Added/Deep"));
        Refresh();
        HostScope(missingFolder);
        Require(window.StatusText.Text.StartsWith("Scan could not start:"), "Unavailable folder did not report a refresh failure");
        window.RootButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout();
        HostScope(clickResult.Root);
        Console.WriteLine("PASS  Failed refresh restores the current map and leaves parent navigation working");

        Write("Click/new-root.dat", 500);
        Refresh();
        var refreshedDrive = window.Map.Root!;
        Require(refreshedDrive.Parent == null && refreshedDrive.FullPath == clickResult.Root.FullPath, "Root refresh lost the drive scope");
        Require(refreshedDrive.Size == oldSize + 4560 && refreshedDrive.Children.Any(node => node.Name == "new-root.dat"), "Root refresh did not include all drive descendants");
        Require(!window.UpButton.IsEnabled && window.RefreshButton.IsEnabled, "Root refresh left navigation controls in the wrong state");
        Console.WriteLine("PASS  Refresh at the root refreshes the entire tree");

        // Reproduce a Downloads-like distribution whose filename order differs
        // from size order. A refresh must not switch to scan-slot partitions.
        Write("Refresh layout/Downloads/zz-memory.dmp", 55600);
        Write("Refresh layout/Downloads/aa-update.msu", 32000);
        Write("Refresh layout/Downloads/mm-model.zip", 23700);
        Write("Refresh layout/Downloads/bb-installer.exe", 11800);
        Write("Refresh layout/Downloads/cc-cad.exe", 9000);
        Write("Refresh layout/Downloads/dd-tools.exe", 8980);
        for (int i = 0; i < 200; i++) Write($"Refresh layout/Downloads/small-{i:000}.bin", 1 + i % 30);
        var (downloadsTree, _, _) = Scan(Path.Combine(fixture, "Refresh layout"));
        window.DisplayCompletedScan(downloadsTree);
        Layout();
        ClickFolder(window.Map, "Downloads");
        Layout();
        var niceLayout = Capture(window.Map);
        Refresh();
        Same(niceLayout, Capture(window.Map));
        Console.WriteLine("PASS  Refresh with unchanged data retains the exact size-sorted Downloads layout");

        var largeBefore = window.Map.VisibleTiles.Where(tile => tile.Node!.Size >= 8980)
            .ToDictionary(tile => tile.Node!.Name, tile => tile.Bounds);
        long downloadsSize = window.Map.Root!.Size;
        File.Delete(Path.Combine(fixture, "Refresh layout/Downloads/small-099.bin"));
        Refresh();
        Require(window.Map.Root!.Size == downloadsSize - 10 && window.Map.Root.Children.Count == 205,
            "Deleted file remained in the refreshed folder's size or entries");
        Require(window.Map.VisibleTiles.All(tile => tile.Node?.Name != "small-099.bin"), "Deleted file still has a visible square");
        foreach (var tile in window.Map.VisibleTiles.Where(tile => largeBefore.ContainsKey(tile.Node!.Name)))
        {
            var prior = largeBefore[tile.Node!.Name];
            Require(Math.Abs(tile.Bounds.X - prior.X) < 8 && Math.Abs(tile.Bounds.Y - prior.Y) < 8
                && Math.Abs(tile.Bounds.Width - prior.Width) < 8 && Math.Abs(tile.Bounds.Height - prior.Height) < 8,
                "Deleting a small file scrambled the large rectangles or turned them into strips");
        }
        Console.WriteLine("PASS  Deleting a small file and refreshing removes its square without scrambling the large blocks");

        Write("Refresh layout/Downloads/new-file.bin", 2000);
        Refresh();
        Require(window.Map.VisibleTiles.Any(tile => tile.Node?.Name == "new-file.bin"), "Refresh omitted a newly created file");
        var afterAddition = Capture(window.Map);
        window.UpButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout();
        ClickFolder(window.Map, "Downloads");
        Layout();
        Same(afterAddition, Capture(window.Map));
        Console.WriteLine("PASS  Added files use the same size-based layout, including after leaving and returning to the folder");

        Write("Root cache/A/Nested/data.bin", 100);
        Write("Root cache/B/data.bin", 100);
        var (liveRootTree, _, liveRootLast) = Scan(Path.Combine(fixture, "Root cache"));
        window.Map.Root = null;
        window.Map.ShowLive(liveRootLast);
        window.DisplayCompletedScan(liveRootTree);
        Layout();
        var oldA = liveRootTree.Root.Children.Single(node => node.Name == "A");
        ClickFolder(window.Map, "A");
        Layout();
        Write("Root cache/A/Nested/data.bin", 900);
        Refresh();
        var currentA = window.Map.Root!;
        window.RootButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout();
        CheckRootBlock("A", currentA, 0.9);
        Require(ReferenceEquals(liveRootTree.Root.ResolveSnapshotNode(oldA), currentA), "A stale node still resolves to the replaced subtree");
        ClickFolder(window.Map, "A");
        Layout();
        Require(ReferenceEquals(window.Map.Root, currentA) && window.Map.Root.Size == 900, "Root click reopened old folder data");
        Console.WriteLine("PASS  A live-scanned root rebinds replaced top-level folders, their proportions and click targets");

        ClickFolder(window.Map, "Nested");
        Layout();
        Write("Root cache/A/Nested/data.bin", 1900);
        Refresh();
        window.RootButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout();
        CheckRootBlock("A", currentA, 0.95);
        ClickFolder(window.Map, "A");
        Layout();
        ClickFolder(window.Map, "Nested");
        Layout();
        Write("Root cache/A/Nested/data.bin", 10);
        Refresh();
        window.RootButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout();
        CheckRootBlock("A", currentA, 10.0 / 110);
        Console.WriteLine("PASS  Nested refresh updates cached ancestor areas after both growth and shrinkage");

        Write("Root cache/new-root.bin", 30);
        Refresh();
        Require(!ReferenceEquals(window.Map.Root, liveRootTree.Root), "Whole-root refresh retained the old tree");
        Require(window.Map.VisibleTiles.Any(tile => tile.Node?.Name == "new-root.bin"), "Whole-root replacement retained obsolete partition data");
        Require(window.Map.VisibleTiles.All(tile => tile.Node == null || ReferenceEquals(tile.Node.Parent, window.Map.Root)), "Whole-root replacement still points into the old tree");
        Console.WriteLine("PASS  Replacing the entire root discards the previous tree and its cached partitions");

        window.DisplayCompletedScan(deep);
        Layout();
        for (int i = 0; i < 64; i++)
        {
            ClickFolder(window.Map, $"L{i:00}");
            Layout();
            HostScope(window.Map.Root!);
            Require(window.Breadcrumbs.Children.OfType<Button>().Count() == i + 2,
                "Real window lost a breadcrumb during deep navigation");
        }
        Require(!window.IsVisible && PresentationSource.FromVisual(window) == null,
            "Headless checks created a desktop window");
        window.Close();
        Console.WriteLine("PASS  Real window navigates 64 levels through production handlers without opening a desktop window");
        Console.WriteLine("\n24 Shell/render checks passed. Temporary fixture will be removed automatically. No desktop windows were opened.");

        void CheckRootBlock(string name, DiskNode expected, double proportion)
        {
            var tile = window.Map.VisibleTiles.Single(tile => tile.Node?.Name == name);
            Require(ReferenceEquals(tile.Node, expected), "Root map retains a stale node");
            double area = (tile.Bounds.Width + 3) * (tile.Bounds.Height + 3);
            Require(Math.Abs(area / (window.Map.ActualWidth * window.Map.ActualHeight) - proportion) < 1e-8,
                "Cached root proportions do not match current byte sizes");
        }

        void Refresh(bool stop = false)
        {
            Require(window.RefreshButton.IsEnabled, "Refresh button is unavailable for a completed view");
            var priorScope = window.Map.Root;
            var priorLayout = Capture(window.Map);
            window.RefreshButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(ReferenceEquals(window.Map.Root, priorScope), "Refresh cleared or replaced the current map before fresh data was available");
            Same(priorLayout, Capture(window.Map));
            Require(!window.RefreshButton.IsEnabled && !window.UpButton.IsEnabled && !window.RootButton.IsEnabled,
                "Navigation was not disabled during refresh");
            if (stop) window.ScanButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var frame = new System.Windows.Threading.DispatcherFrame();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var poll = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            poll.Tick += (_, _) =>
            {
                if (!window.RefreshButton.IsEnabled)
                {
                    Require(ReferenceEquals(window.Map.Root, priorScope), "Refresh progress replaced the completed map with placeholder partitions");
                    Same(priorLayout, Capture(window.Map));
                }
                if (window.RefreshButton.IsEnabled || clock.Elapsed > TimeSpan.FromSeconds(15)) frame.Continue = false;
            };
            poll.Start();
            try { System.Windows.Threading.Dispatcher.PushFrame(frame); }
            finally { poll.Stop(); }
            Require(window.RefreshButton.IsEnabled, "Refresh did not finish within 15 seconds");
            Layout();
        }

        void Layout()
        {
            surface.Measure(new Size(1380, 900));
            surface.Arrange(new Rect(0, 0, 1380, 900));
            surface.UpdateLayout();
            Capture(window.Map);
        }

        void HostScope(DiskNode folder)
        {
            Require(ReferenceEquals(window.Map.Root, folder), "Real window's map did not navigate to the selected folder");
            Require(window.MapTitle.Text == folder.Name, "Real window's heading did not follow map scope");
            Require(window.Entries.Items.Count == folder.Children.Count, "Real window's list did not show current contents");
            Require((string)window.Breadcrumbs.Children.OfType<Button>().Last().Content == folder.Name,
                "Real window's breadcrumb did not follow map scope");
            ImmediateScope(window.Map);
        }

        void Write(string relative, long length)
        {
            string path = Path.Combine(fixture, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = File.Create(path);
            stream.SetLength(length);
        }
    }

    private static (ScanResult Result, DiskNode First, DiskNode Last) Scan(string path)
    {
        var snapshots = new ConcurrentQueue<DiskNode>();
        var result = new DiskScanner().ScanAsync(path, 4, new Observer(value =>
        {
            if (value.MapRoot != null) snapshots.Enqueue(value.MapRoot);
        })).GetAwaiter().GetResult();
        return (result, snapshots.First(), snapshots.Last());
    }

    private static TreemapView CreateMap()
    {
        var map = new TreemapView();
        map.Measure(new Size(900, 600));
        map.Arrange(new Rect(0, 0, 900, 600));
        map.UpdateLayout();
        return map;
    }

    private static IReadOnlyList<(Rect Bounds, Color Color)> Capture(TreemapView map, string? name = null)
    {
        map.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(map.ActualWidth), (int)Math.Ceiling(map.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(map);
        if (_saveRenders && name != null)
        {
            string directory = Path.GetFullPath(Path.Combine("artifacts", "render-checks"));
            Directory.CreateDirectory(directory);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(directory, name + ".png"));
            encoder.Save(stream);
        }
        var boxes = new List<(Rect, Color)>();
        Read(VisualTreeHelper.GetDrawing(map));
        Require(boxes.Count > 0, "No map blocks rendered");
        return boxes;

        void Read(Drawing? drawing)
        {
            if (drawing is DrawingGroup group) foreach (var child in group.Children) Read(child);
            else if (drawing is GeometryDrawing { Geometry: RectangleGeometry { RadiusX: 3 } geometry, Brush: SolidColorBrush brush }) boxes.Add((geometry.Rect, brush.Color));
        }
    }

    private static void Same(IReadOnlyList<(Rect Bounds, Color Color)> before, IReadOnlyList<(Rect Bounds, Color Color)> after)
    {
        Require(before.Count == after.Count, "Blocks were removed or consolidated at completion");
        for (int i = 0; i < before.Count; i++)
        {
            Require(before[i].Color == after[i].Color, "A block changed identity/color");
            var a = before[i].Bounds; var b = after[i].Bounds;
            Require(Math.Abs(a.X - b.X) < 1e-8 && Math.Abs(a.Y - b.Y) < 1e-8 && Math.Abs(a.Width - b.Width) < 1e-8 && Math.Abs(a.Height - b.Height) < 1e-8, $"A block jumped at completion: {a} -> {b}");
        }
    }

    private static Point Center(MapRect rect) => new(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);

    private static void ImmediateScope(TreemapView map)
        => Require(map.VisibleTiles.All(tile => tile.Container == map.Root &&
            (tile.Node == null || tile.Node.IsAggregate || tile.Node.Parent == map.Root)),
            "A box represents a descendant outside the current folder's immediate items");

    private static void ClickFolder(TreemapView map, string name)
    {
        var rect = map.VisibleTiles.Single(tile => tile.Node?.Name == name).Bounds;
        Require(Click(map, new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2)), $"Folder {name} did not open");
    }

    private static bool Click(TreemapView map, Point point)
        => map.ActivateAt(point, MouseButton.Left);

    private static void CheckChildBoxes(DiskNode folder, IReadOnlyList<(Rect Bounds, Color Color)> boxes)
    {
        var children = folder.Children.Where(node => node.Size > 0).ToArray();
        Require(boxes.Count == children.Length, "Children were collapsed into a single selected-folder block");
        for (int i = 0; i < children.Length; i++)
        {
            var rect = boxes[i].Bounds;
            // Restore the 1.5px inset on each edge to check the actual layout area.
            double share = (rect.Width + 3) * (rect.Height + 3) / (900 * 600);
            Require(Math.Abs(share - (double)children[i].Size / folder.Size) < 1e-9, "Drilled child areas are not proportional to their sizes");
            Require(boxes[i].Color == ((SolidColorBrush)TreemapView.ColorFor(children[i])).Color, "Drilled box does not represent the expected child");
        }
    }

    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Observer(Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => report(value);
    }
}
