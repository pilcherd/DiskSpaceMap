using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DiskSpaceMap.Core;

namespace DiskSpaceMap;

public partial class MainWindow : Window
{
    private sealed record DriveChoice(string Path, string Label);
    private sealed record EntryRow(DiskNode Node, long ParentSize)
    {
        public string Name => ShellDisplayNames.Get(Node);
        public long Size => Node.Size;
        public string SizeText => Node.ScanError != null && Size == 0 ? "Unreadable"
            : Node.IsDirectory && !Node.WasEnumerated && Size == 0 && !Node.IsDirectoryLink ? "Waiting…" : SizeFormatter.Format(Size);
        public string ShareText => Node.IsDirectoryLink ? "link · skipped" : Node.ScanError != null ? "unreadable" : $"{(ParentSize > 0 ? (double)Size / ParentSize : 0):P1}";
        public string Symbol => Node.IsDirectory ? "\uE8B7" : "\uE7C3";
        public Brush Color => TreemapView.ColorFor(Node);
    }

    private ScanResult? _result;
    private ScanResult? _scanDetails;
    private readonly FolderNavigator _navigation = new();
    private DiskNode? _current => _navigation.Current;
    private DiskNode? _selected;
    private CancellationTokenSource? _scanCancellation;
    private bool _synchronizing;
    private IReadOnlyList<EntryRow> _rows = [];
    private readonly System.Windows.Threading.DispatcherTimer _nameRefresh = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private int _nameRevision;

    public MainWindow() : this(scanOnOpening: true) { }

    internal MainWindow(bool scanOnOpening)
    {
        InitializeComponent();
        Title = $"DiskSpaceMap {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}";
        _navigation.Changed += PresentFolder;
        _nameRefresh.Tick += (_, _) => RefreshDisplayNames();
        Loaded += (_, _) => _nameRefresh.Start();
        Closed += (_, _) => _nameRefresh.Stop();
        if (scanOnOpening) InitializeDriveScanning();
        Closing += (_, _) => _scanCancellation?.Cancel();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowStartupSizing.Fit(this);
    }

    private void InitializeDriveScanning()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                string label = string.IsNullOrEmpty(drive.VolumeLabel) ? drive.DriveType.ToString() : drive.VolumeLabel;
                DrivePicker.Items.Add(new DriveChoice(drive.Name, $"{drive.Name}  {label} · {SizeFormatter.Format(drive.TotalSize)}"));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        DrivePicker.SelectedItem = DrivePicker.Items.Cast<DriveChoice>()
            .FirstOrDefault(drive => string.Equals(drive.Path, @"C:\", StringComparison.OrdinalIgnoreCase))
            ?? DrivePicker.Items.Cast<DriveChoice>().FirstOrDefault();
        Loaded += async (_, _) => await StartScanAsync();
    }

    private async void Drive_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && _scanCancellation == null) await StartScanAsync();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCancellation != null)
        {
            _scanCancellation.Cancel();
            ScanButton.IsEnabled = false;
            StatusText.Text = "Stopping scan and preparing the partial map…";
            return;
        }
        await StartScanAsync();
    }

    private async Task StartScanAsync(DiskNode? refreshTarget = null)
    {
        if (_scanCancellation != null) return;
        var previous = _result;
        var previousDetails = _scanDetails;
        if (refreshTarget != null && previous == null) return;
        string? path = refreshTarget?.FullPath ?? (DrivePicker.SelectedItem as DriveChoice)?.Path;
        if (path == null) { StatusText.Text = "No ready drives found."; return; }
        long retainedBytes = refreshTarget == null ? 0 : previous!.Root.Size - refreshTarget.Size;
        long retainedFiles = refreshTarget == null ? 0 : previous!.Root.FileCount - refreshTarget.FileCount;
        long retainedFolders = refreshTarget == null ? 0 : previous!.Root.DirectoryCount - refreshTarget.DirectoryCount;
        using var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        SetScanning(true);
        _result = null;
        if (refreshTarget == null) _navigation.BeginScan();
        _selected = null;
        if (refreshTarget == null)
        {
            Map.Root = null;
            _rows = [];
            Entries.ItemsSource = null;
            FilterBox.Text = "";
        }
        if (refreshTarget == null)
        {
            Breadcrumbs.Children.Clear();
            Breadcrumbs.Children.Add(new TextBlock { Text = path, Foreground = Brushes.LightGray });
        }
        if (refreshTarget == null)
        {
            EmptyState.Visibility = Visibility.Visible;
            EmptyDescription.Text = "Discovering this location’s folders and files…";
        }
        MapSubtitle.Text = refreshTarget == null ? "Mapping your location…" : "Refreshing this folder and all subfolders…";
        MapTitle.Text = refreshTarget == null ? path : ShellDisplayNames.Get(refreshTarget);
        MapTitle.ToolTip = path;
        FilesStat.Text = FoldersStat.Text = BytesStat.Text = TimeStat.Text = "—";
        RateStat.Text = "";
        ItemCount.Text = "";
        WarningsButton.Visibility = Visibility.Hidden;
        var progress = new Progress<ScanProgress>(value =>
        {
            if (_scanCancellation != cancellation || cancellation.IsCancellationRequested || _result != null) return;
            UpdateStats(retainedBytes + value.Bytes, retainedFiles + value.Files, retainedFolders + value.Directories, value.Elapsed);
            StatusText.Text = $"Scanning  ·  {value.CurrentPath}";
            StatusText.ToolTip = value.CurrentPath;
            // Refresh keeps the completed, size-sorted map until the replacement
            // tree is ready. Live scan slots follow enumeration order and must not
            // replace a squarified folder view or become its final topology.
            if (refreshTarget == null && value.MapRoot is { } snapshot)
            {
                string? selectedName = _selected?.Name;
                if (refreshTarget == null) _navigation.Preview(snapshot);
                Map.ShowLive(snapshot);
                _rows = new LazyRows(snapshot);
                _synchronizing = true;
                ApplyFilter();
                Entries.SelectedItem = Entries.Items.Cast<EntryRow>().FirstOrDefault(row => row.Node.Name == selectedName);
                _synchronizing = false;
                Select(snapshot.Children.FirstOrDefault(node => node.Name == selectedName));
                OpenCurrentButton.IsEnabled = true;
                EmptyState.Visibility = snapshot.Children.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
                MapSubtitle.Text = "Live sizes · unmeasured folders start as placeholders";
            }
        });
        try
        {
            // Includes validation off the UI thread; network paths may respond slowly.
            var result = await Task.Run(() => new DiskScanner().ScanAsync(path,
                progress: progress, cancellationToken: cancellation.Token));
            if (refreshTarget == null) DisplayCompletedScan(result);
            else if (result.WasCancelled || cancellation.IsCancellationRequested)
            {
                RestorePreviousView();
                StatusText.Text = "Refresh stopped · previous view kept";
                StatusText.ToolTip = StatusText.Text;
            }
            else
            {
                var refreshed = refreshTarget.ReplaceWith(result.Root);
                var root = refreshed.Parent == null ? refreshed : previous!.Root;
                DisplayCompletedScan(result with { Root = root }, refreshed);
                StatusText.Text = "Refreshed  ·  " + StatusText.Text;
                StatusText.ToolTip = StatusText.Text;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            if (refreshTarget != null) RestorePreviousView();
            StatusText.Text = "Scan could not start: " + ex.Message;
            StatusText.ToolTip = ex.ToString();
            if (refreshTarget == null)
            {
                EmptyDescription.Text = "This drive could not be scanned.\nCheck your access or select another drive.";
                MapSubtitle.Text = "Location unavailable";
            }
        }
        finally
        {
            _scanCancellation = null;
            SetScanning(false);
        }

        void RestorePreviousView()
        {
            DisplayCompletedScan(previous!, refreshTarget);
            _scanDetails = previousDetails;
        }
    }

    internal void DisplayCompletedScan(ScanResult result, DiskNode? currentFolder = null)
    {
        _result = result;
        _scanDetails = result with { Root = currentFolder ?? result.Root };
        UpdateStats(result.Root.Size, result.Root.FileCount, result.Root.DirectoryCount, result.Elapsed);
        _navigation.Complete(result.Root, currentFolder);
        string outcome = result.WasCancelled ? "Stopped · partial map" : result.Errors > 0 ? "Complete · some folders could not be read" : "Scan complete";
        StatusText.Text = $"{outcome}  ·  {result.Elapsed.TotalSeconds:0.00}s  ·  {result.WorkerCount} workers  ·  {result.Errors:N0} unreadable  ·  {result.SkippedLinks:N0} directory links skipped";
        StatusText.ToolTip = StatusText.Text;
        WarningsButton.Visibility = Visibility.Visible;
        SetScanning(false);
    }

    private void SetScanning(bool scanning)
    {
        DrivePicker.IsEnabled = !scanning;
        ScanButton.IsEnabled = true;
        ScanButton.Content = scanning ? "Stop scan" : "Rescan drive";
        // Reserve its height so starting/stopping refresh never resizes the map.
        ScanProgressBar.Visibility = scanning ? Visibility.Visible : Visibility.Hidden;
        UpButton.IsEnabled = !scanning && _current?.Parent != null;
        RootButton.IsEnabled = OpenCurrentButton.IsEnabled = !scanning && _current != null;
        RefreshButton.IsEnabled = !scanning && _result != null && _current != null;
    }

    private void UpdateStats(long bytes, long files, long folders, TimeSpan elapsed)
    {
        BytesStat.Text = SizeFormatter.Format(bytes);
        FilesStat.Text = files.ToString("N0");
        FoldersStat.Text = folders.ToString("N0");
        TimeStat.Text = elapsed.TotalSeconds < 60 ? $"{elapsed.TotalSeconds:0.0}s" : $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s";
        RateStat.Text = elapsed.TotalSeconds > 0 ? $"{files / elapsed.TotalSeconds:N0} files/s" : "";
    }

    private static bool CanDrill(DiskNode? node) => node is { IsDirectory: true, IsDirectoryLink: false };

    private void Navigate(DiskNode node)
    {
        if (_scanCancellation != null) return;
        _navigation.Open(node);
    }

    private void PresentFolder(DiskNode node)
    {
        _selected = null;
        Map.Root = node;
        MapTitle.Text = ShellDisplayNames.Get(node);
        MapTitle.ToolTip = node.FullPath;
        FilterBox.Text = "";
        _rows = new LazyRows(node);
        ApplyFilter();
        MapSubtitle.Text = $"{SizeFormatter.Format(node.Size)}  ·  {node.FileCount:N0} files  ·  {node.DirectoryCount:N0} folders";
        EmptyState.Visibility = node.Size == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyDescription.Text = node.ScanError != null ? "This folder could not be read: " + node.ScanError
            : !node.WasEnumerated ? "This folder was not scanned before the scan stopped."
            : "There are no files with a nonzero size in this folder.";
        Breadcrumbs.Children.Clear();
        foreach (var ancestor in _navigation.Breadcrumbs)
        {
            var target = ancestor;
            var button = new Button { Content = ShellDisplayNames.Get(ancestor), Tag = ancestor, Padding = new Thickness(6, 5, 6, 5), Background = Brushes.Transparent, ToolTip = ancestor.FullPath, Foreground = ancestor == node ? Brushes.White : new SolidColorBrush(Color.FromRgb(148, 164, 185)) };
            button.Click += (_, _) => Navigate(target);
            Breadcrumbs.Children.Add(button);
            if (ancestor != node) Breadcrumbs.Children.Add(new TextBlock { Text = "›", VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray });
        }
        UpButton.IsEnabled = node.Parent != null;
        RootButton.IsEnabled = OpenCurrentButton.IsEnabled = true;
        Select(null);
    }

    private void RefreshDisplayNames()
    {
        int revision = ShellDisplayNames.Revision;
        if (_nameRevision == revision) return;
        _nameRevision = revision;
        Map.RefreshDisplayNames();
        if (_current != null) MapTitle.Text = ShellDisplayNames.Get(_current);
        foreach (var button in Breadcrumbs.Children.OfType<Button>())
            if (button.Tag is DiskNode node) button.Content = ShellDisplayNames.Get(node);
        if (!string.IsNullOrWhiteSpace(FilterBox.Text)) ApplyFilter();
        // Update only realized rows; keep the virtualized collection and selection intact.
        RefreshBindings(Entries);

        static void RefreshBindings(DependencyObject parent)
        {
            if (parent is TextBlock text)
            {
                text.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
                text.GetBindingExpression(ToolTipProperty)?.UpdateTarget();
            }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                RefreshBindings(VisualTreeHelper.GetChild(parent, i));
        }
    }

    // Indexed lazy wrappers keep navigation in directories with millions of entries cheap.
    private sealed class LazyRows(DiskNode parent) : IReadOnlyList<EntryRow>, System.Collections.IList
    {
        public int Count => parent.Children.Count;
        public EntryRow this[int index] => new(parent.Children[index], parent.Size);
        public IEnumerator<EntryRow> GetEnumerator() { for (int i = 0; i < Count; i++) yield return this[i]; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        object? System.Collections.IList.this[int index] { get => this[index]; set => throw new NotSupportedException(); }
        public bool IsReadOnly => true;
        public bool IsFixedSize => true;
        public bool IsSynchronized => false;
        public object SyncRoot => this;
        public int Add(object? value) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public void Insert(int index, object? value) => throw new NotSupportedException();
        public void Remove(object? value) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
        public bool Contains(object? value) => IndexOf(value) >= 0;
        public int IndexOf(object? value) => value is EntryRow row ? ((IList<DiskNode>)parent.Children).IndexOf(row.Node) : -1;
        public void CopyTo(Array array, int index) { for (int i = 0; i < Count; i++) array.SetValue(this[i], index + i); }
    }

    private void ApplyFilter()
    {
        if (Entries == null) return;
        string filter = FilterBox.Text.Trim();
        // No collection-view filter on the fast path, so WPF can virtualize the underlying IList.
        Entries.ItemsSource = filter.Length == 0 ? _rows : _rows.Where(x => x.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || x.Node.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        ItemCount.Text = $"{Entries.Items.Count:N0} items";
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void Map_FolderInvoked(DiskNode node) => Navigate(node);
    private void Map_LocationInvoked(DiskNode node) => OpenLocation(node, !node.IsDirectory);
    private void Map_SelectionChanged(DiskNode? node)
    {
        Select(node);
        _synchronizing = true;
        Entries.SelectedItem = null;
        if (node?.Parent == _current)
        {
            foreach (EntryRow row in Entries.Items)
                if (row.Node == node) { Entries.SelectedItem = row; Entries.ScrollIntoView(row); break; }
        }
        _synchronizing = false;
    }

    private void Entries_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_synchronizing) Select((Entries.SelectedItem as EntryRow)?.Node);
    }

    private void Select(DiskNode? node)
    {
        _selected = node;
        Map.Selected = node;
    }

    private void Entries_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Header double-clicks and blank space must not invoke an old selection.
        if (ItemsControl.ContainerFromElement(Entries, e.OriginalSource as DependencyObject) is DataGridRow row && row.Item is EntryRow entry && CanDrill(entry.Node)) Navigate(entry.Node);
    }

    private void Entries_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _selected == null) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) OpenLocation(_selected, true);
        else if (CanDrill(_selected)) Navigate(_selected);
        e.Handled = true;
    }

    private void Entries_Sorting(object sender, DataGridSortingEventArgs e)
    {
        // Explicit sorting is opt-in; scan completion/navigation uses the pre-sorted core list.
        e.Handled = true;
        var direction = e.Column.SortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        var rows = Entries.Items.Cast<EntryRow>();
        Entries.ItemsSource = e.Column.SortMemberPath == "Size"
            ? direction == ListSortDirection.Ascending ? rows.OrderBy(x => x.Size).ToArray() : rows.OrderByDescending(x => x.Size).ToArray()
            : direction == ListSortDirection.Ascending ? rows.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray() : rows.OrderByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var column in Entries.Columns) column.SortDirection = null;
        e.Column.SortDirection = direction;
    }

    private void Up_Click(object sender, RoutedEventArgs e) => _navigation.Up();
    private void Root_Click(object sender, RoutedEventArgs e) => _navigation.Root();
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCancellation == null && _result != null && _current is { } folder)
            await StartScanAsync(folder);
    }
    private void OpenCurrent_Click(object sender, RoutedEventArgs e) { if (_current != null) OpenLocation(_current, false); }

    private void Entries_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(Entries, e.OriginalSource as DependencyObject) is DataGridRow row)
            Entries.SelectedItem = row.Item;
        else Entries.SelectedItem = null;
    }

    private void Entries_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        OpenEntryMenu.IsEnabled = Entries.SelectedItem is EntryRow || _current != null;
        DrillEntryMenu.IsEnabled = _result != null && Entries.SelectedItem is EntryRow row && CanDrill(row.Node);
    }

    private void OpenEntry_Click(object sender, RoutedEventArgs e)
    {
        DiskNode? node = (Entries.SelectedItem as EntryRow)?.Node ?? _current;
        if (node != null) OpenLocation(node, !node.IsDirectory);
    }

    private void DrillEntry_Click(object sender, RoutedEventArgs e)
    {
        if (Entries.SelectedItem is EntryRow row) Navigate(row.Node);
    }

    private void OpenLocation(DiskNode node, bool select)
    {
        try
        {
            string path = node.FullPath;
            if (node.IsAggregate) select = false;
            // Explorer arguments are passed directly, never through a command shell.
            Process.Start(new ProcessStartInfo("explorer.exe", select ? $"/select,\"{path}\"" : $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        { StatusText.Text = "Could not open Explorer: " + ex.Message; }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _scanCancellation != null) { _scanCancellation.Cancel(); e.Handled = true; }
        if (e.Key == Key.F5)
        {
            if (RefreshButton.IsEnabled) Refresh_Click(sender, e);
            e.Handled = true;
            return;
        }
        if (Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is ComboBox) return;
        if (e.Key == Key.Back || e.Key == Key.Up && Keyboard.Modifiers == ModifierKeys.Alt)
        { if (_scanCancellation == null) _navigation.Up(); e.Handled = true; }
    }

    private void Warnings_Click(object sender, RoutedEventArgs e)
    {
        if (_scanDetails is not { } scan) return;
        string details = $"{(scan.WasCancelled ? "PARTIAL SCAN — stopped by user" : "Scan finished")}\n{scan.Root.FullPath}\n\n{scan.Root.FileCount:N0} files · {scan.Root.DirectoryCount:N0} folders\n{SizeFormatter.Format(scan.Root.Size)} logical file size\n{scan.Elapsed.TotalSeconds:0.000} seconds · {scan.WorkerCount} workers\n\n{scan.Errors:N0} unreadable directories\n{scan.SkippedLinks:N0} directory links/junctions skipped\n\nSizes are logical lengths, not allocated space. Compressed, sparse and online-only files may use less disk space. Hard links are counted per name. Protected system data, file-system metadata, alternate streams, and the recycle bin may be missing or incomplete. Files can change during a scan.";
        if (scan.Warnings.Count > 0) details += "\n\nFirst errors (up to 100):\n" + string.Join("\n", scan.Warnings);
        var text = new TextBox { Text = details, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(16), BorderThickness = new Thickness(0) };
        new Window { Title = "Scan details", Owner = this, Width = 700, Height = 540, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = text }.ShowDialog();
    }
}
