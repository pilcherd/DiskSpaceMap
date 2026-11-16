using System.Diagnostics;
using DiskSpaceMap.Core;
using DiskSpaceMap.TestSupport;

try { await RunChecks(args); }
catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }

static async Task RunChecks(string[] args)
{
if (args is ["--benchmark", var path])
{
    await Benchmark(path);
    return;
}

using var temporary = new TemporaryDirectory();
string fixture = temporary.Path;
int passed = 0;
await Check("Exact totals, large lengths, Unicode, empty entries and descending order", async () =>
{
    Write("Documents/report.txt", 1024);
    Write("Documents/zero.bin", 0);
    Write("Media/雪 🗺.bin", 8192);
    Write("root.txt", 11);
    Directory.CreateDirectory(Path.Combine(fixture, "Empty"));
    // Sparse logical length >4 GiB tests the native high DWORD without consuming that disk space.
    string sparse = Path.Combine(fixture, "Media", "sparse.bin");
    using (var stream = File.Create(sparse))
    {
        if (!MarkSparse(stream)) throw new Exception("Could not create the sparse test fixture.");
        stream.SetLength((1L << 32) + 7);
    }
    long expectedSize = (1L << 32) + 7 + 1024 + 8192 + 11;
    foreach (int workers in new[] { 1, 4, 8 })
    {
        var result = await new DiskScanner().ScanAsync(fixture, workers);
        Require(result.Root.Size == expectedSize, $"Expected {expectedSize}, got {result.Root.Size}");
        Require(result.Root.FileCount == 5 && result.Root.DirectoryCount == 3, "Entry totals differ");
        Require(!result.WasCancelled && result.Errors == 0, "Unexpected incomplete scan");
        ValidateTree(result.Root);
        Require(result.Root.Children[0].Name == "Media", "Wrong sort order");
    }
});

await Check("Directory junction loops are skipped", async () =>
{
    string linkPath = Path.Combine(fixture, "Documents", "loop");
    // Junction creation needs no administrator privileges on Windows.
    CreateJunction(linkPath, fixture);
    var result = await new DiskScanner().ScanAsync(fixture, 4);
    Require(result.SkippedLinks == 1, "Junction not skipped");
    Require(result.Root.FileCount == 5, "Junction content counted twice");
    Require(result.Root.DirectoryCount == 4, "Junction missing from folder count");
    var documents = result.Root.Children.Single(n => n.Name == "Documents");
    Require(documents.Children.Single(n => n.Name == "loop").IsDirectoryLink, "Link marker missing");
});

await Check("Pre-cancelled scans return a consistent partial tree", async () =>
{
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var result = await new DiskScanner().ScanAsync(fixture, 4, cancellationToken: cancellation.Token);
    Require(result.WasCancelled && result.Root.FileCount == 0, "Cancellation not observed");
    Require(!result.Root.WasEnumerated, "Unscanned root reported complete");
});

await Check("Long directory paths work", async () =>
{
    string relative = string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat("long-directory-name-1234567890", 12));
    Write(relative + "/deep.bin", 73);
    var result = await new DiskScanner().ScanAsync(Path.Combine(fixture, relative), 2);
    Require(result.Root.Size == 73 && result.Root.FileCount == 1, "Long path not scanned");
});

await Check("Missing locations fail explicitly", async () =>
{
    try { await new DiskScanner().ScanAsync(Path.Combine(fixture, "missing")); }
    catch (DirectoryNotFoundException) { return; }
    throw new Exception("Missing folder was silently accepted");
});

await Check("Treemap preserves area and each file's proportion", () =>
{
    var random = new Random(42);
    foreach (int count in new[] { 1, 2, 3, 50, 2000 })
    {
        long[] weights = Enumerable.Range(0, count).Select(_ => (long)random.Next(1, 1_000_000)).OrderDescending().ToArray();
        foreach (var bounds in new[] { new MapRect(0, 0, 900, 600), new MapRect(11, 17, 70, 1000) })
        {
            var tiles = TreemapLayout.Layout(weights, bounds, 120);
            double area = bounds.Width * bounds.Height, total = weights.Sum(x => (double)x);
            Require(tiles.Count <= 120, "Tile budget exceeded");
            Require(Math.Abs(tiles.Sum(t => t.Bounds.Width * t.Bounds.Height) - area) < area * 1e-9, "Lost map area");
            foreach (var tile in tiles)
            {
                var r = tile.Bounds;
                Require(double.IsFinite(r.Width) && r.Width >= 0 && r.Height >= 0, "Invalid rectangle");
                Require(r.X >= bounds.X - 1e-8 && r.Y >= bounds.Y - 1e-8 && r.X + r.Width <= bounds.X + bounds.Width + 1e-8 && r.Y + r.Height <= bounds.Y + bounds.Height + 1e-8, "Out of bounds");
                if (!tile.IsRemainder) Require(Math.Abs(r.Width * r.Height / area - weights[tile.Index] / total) < 1e-9, "Wrong area ratio");
            }
            for (int i = 0; i < tiles.Count; i++)
                for (int j = i + 1; j < tiles.Count; j++)
                {
                    var a = tiles[i].Bounds; var b = tiles[j].Bounds;
                    double overlap = Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X)) * Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
                    Require(overlap < 1e-6, "Overlapping tiles");
                }
        }
    }
    Require(TreemapLayout.Layout([0, 0], new(0, 0, 200, 100)).Count == 0, "Zero files rendered");
    Require(TreemapLayout.Layout([1], new(0, 0, 0, 100)).Count == 0, "Zero-width map rendered");
    return Task.CompletedTask;
});

await Check("Ordered live partitions preserve proportions and never overlap while weights change", () =>
{
    var random = new Random(91);
    double[] target = Enumerable.Range(0, 64).Select(i => i % 3 == 0 ? 0d : random.Next(1, 1000000)).ToArray();
    for (int frame = 0; frame <= 20; frame++)
    {
        double t = frame / 20d;
        double[] weights = target.Select(weight => 1 + (weight - 1) * t).ToArray();
        var tiles = TreemapLayout.LayoutOrdered(weights, new(0, 0, 900, 600));
        double total = weights.Sum(), area = 900 * 600;
        Require(Math.Abs(tiles.Sum(tile => tile.Bounds.Width * tile.Bounds.Height) - area) < 1e-6, "Live map has gaps");
        foreach (var tile in tiles)
            Require(Math.Abs(tile.Bounds.Width * tile.Bounds.Height / area - weights[tile.Index] / total) < 1e-9, "Live area ratio differs");
        for (int i = 0; i < tiles.Count; i++)
            for (int j = i + 1; j < tiles.Count; j++)
            {
                var a = tiles[i].Bounds; var b = tiles[j].Bounds;
                double overlap = Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X)) * Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
                Require(overlap < 1e-6, "Live partitions overlap");
            }
    }
    return Task.CompletedTask;
});

await Check("Busy directory scans don't lose files across workers", async () =>
{
    string busy = Path.Combine(fixture, "Many files");
    Directory.CreateDirectory(busy);
    for (int folder = 0; folder < 100; folder++)
        for (int file = 0; file < 100; file++) Write($"Many files/Folder {folder:000}/File {file:000}.dat", file + 1);
    var scanned = await new DiskScanner().ScanAsync(busy, 4);
    Require(scanned.Root.FileCount == 10_000 && scanned.Root.Size == 505_000, "Concurrent enumeration lost files");
    ValidateTree(scanned.Root);
});

await Check("Live map appears before descendants, grows to exact totals and snapshots stay immutable", async () =>
{
    var snapshots = new List<ScanProgress>();
    var fingerprints = new List<string>();
    var observer = new CallbackProgress(value =>
    {
        lock (snapshots)
        {
            Require(value.MapRoot != null, "Missing live map");
            Require(value.MapRoot.Children.Count <= 512, "Unbounded snapshot");
            snapshots.Add(value);
            fingerprints.Add(Fingerprint(value.MapRoot));
        }
    });
    var result = await new DiskScanner().ScanAsync(fixture, 4, observer);
    Require(snapshots.Count >= 2, "Initial/final snapshot missing");
    var first = snapshots[0].MapRoot!;
    Require(first.Children.Single(n => n.Name == "Many files").Size == 0, "Descendants were scanned before placeholders appeared");
    Require(!first.Children.Single(n => n.Name == "Many files").WasEnumerated, "Unscanned branch marked complete");
    Require(first.Children.Single(n => n.Name == "root.txt").Size == 11, "Root file size missing");
    var last = snapshots[^1].MapRoot!;
    Require(last.Size == result.Root.Size && last.FileCount == result.Root.FileCount && last.DirectoryCount == result.Root.DirectoryCount, "Live totals disagree with final scan");
    foreach (var child in last.Children)
    {
        var actual = result.Root.Children.Single(n => n.Name == child.Name);
        Require(child.Size == actual.Size && child.FileCount == actual.FileCount && child.DirectoryCount == actual.DirectoryCount, "Live branch totals disagree");
        Require(child.WasEnumerated, "Completed branch still pending");
    }
    for (int i = 0; i < snapshots.Count; i++) Require(Fingerprint(snapshots[i].MapRoot!) == fingerprints[i], "Published snapshot changed");
    Require(snapshots.Count <= result.Elapsed.TotalMilliseconds / 150 + 3, "Progress callbacks not throttled");
});

await Check("Cancellation immediately after placeholders returns a safe partial result", async () =>
{
    using var cancellation = new CancellationTokenSource();
    var observer = new CallbackProgress(_ => cancellation.Cancel());
    var result = await new DiskScanner().ScanAsync(fixture, 4, observer, cancellation.Token);
    Require(result.WasCancelled, "Cancellation from the initial map was ignored");
    Require(result.Root.Children.Count > 0, "Discovered root entries were lost");
    Require(result.Root.Children.Single(n => n.Name == "Many files").FileCount == 0, "Descendants ran before first publication");
    ValidateTree(result.Root);
});

await Check("Very wide roots keep live snapshots bounded without losing bytes or folder counts", async () =>
{
    string wide = Path.Combine(fixture, "Wide root");
    for (int i = 0; i < 540; i++) Write($"Wide root/Folder {i:000}/data.bin", i + 1);
    ScanProgress? last = null;
    var observer = new CallbackProgress(value => last = value);
    var result = await new DiskScanner().ScanAsync(wide, 4, observer);
    var snapshot = last!.MapRoot!;
    Require(snapshot.Children.Count == 512, "Snapshot cap not applied");
    var overflow = snapshot.Children.Single(n => n.IsAggregate);
    Require(overflow.FullPath == result.Root.FullPath, "Aggregate opens a nonexistent path");
    Require(snapshot.Size == result.Root.Size && snapshot.FileCount == 540 && snapshot.DirectoryCount == 540, "Overflow totals lost data");
    Require(snapshot.Size == snapshot.Children.Sum(n => n.Size), "Overflow bytes do not add up");
    Require(snapshot.Children.All(n => n.WasEnumerated), "Overflow completion not observed");
});

await Check("Scoped refresh replaces descendants and updates ancestors without rescanning siblings", async () =>
{
    Write("Refresh/Changed/Nested/old.bin", 10);
    Write("Refresh/Outside/keep.bin", 30);
    var original = await new DiskScanner().ScanAsync(Path.Combine(fixture, "Refresh"));
    var changed = original.Root.Children.Single(node => node.Name == "Changed");
    var removedFile = changed.Children.Single().Children.Single();
    var outside = original.Root.Children.Single(node => node.Name == "Outside");
    // This unrelated change must not be included in a refresh of Changed.
    Write("Refresh/Outside/keep.bin", 300);
    File.Delete(Path.Combine(fixture, "Refresh/Changed/Nested/old.bin"));
    Write("Refresh/Changed/Nested/Deeper/new.bin", 100);
    var fresh = await new DiskScanner().ScanAsync(changed.FullPath);
    var replacement = changed.ReplaceWith(fresh.Root);
    Require(ReferenceEquals(original.Root.ResolveSnapshotNode(changed), replacement), "An old folder reference reopened the replaced subtree");
    Require(original.Root.ResolveSnapshotNode(removedFile) == null, "An old file reference resolved to a deleted file");
    Require(replacement.Parent == original.Root && replacement.Name == "Changed", "Refreshed folder lost its ancestry");
    Require(ReferenceEquals(original.Root.Children[0], replacement), "Refreshed folder was not sorted by its new size");
    Require(original.Root.Size == 130 && original.Root.FileCount == 2 && original.Root.DirectoryCount == 4, "Ancestor totals are stale");
    Require(outside.Size == 30 && ReferenceEquals(original.Root.Children[1], outside), "Unrelated subtree was rescanned or replaced");
    Require(replacement.Children[0].Children[0].Children[0].FullPath == Path.GetFullPath(Path.Combine(fixture, "Refresh/Changed/Nested/Deeper/new.bin")), "Refreshed descendants have incorrect paths");
    ValidateTree(original.Root);
    var wrongFolder = await new DiskScanner().ScanAsync(outside.FullPath);
    try { replacement.ReplaceWith(wrongFolder.Root); }
    catch (ArgumentException) { return; }
    throw new Exception("Refresh accepted a different folder");
});

await Check("Progressive map rebinds current nodes and zeros deleted entries", async () =>
{
    Write("Map refresh/a.bin", 10);
    Write("Map refresh/b.bin", 20);
    var before = await new DiskScanner().ScanAsync(Path.Combine(fixture, "Map refresh"));
    var map = new ProgressiveMap();
    var original = map.Update(before.Root, true);
    File.Delete(Path.Combine(fixture, "Map refresh/a.bin"));
    Write("Map refresh/b.bin", 90);
    var after = await new DiskScanner().ScanAsync(before.Root.FullPath);
    var updated = map.Update(after.Root, true);
    Require(original.Select(entry => entry.Key).SequenceEqual(updated.Select(entry => entry.Key)), "Rebinding reordered settled partitions");
    Require(updated.Single(entry => entry.Node.Name == "a.bin") is { Weight: 0, Node.FileCount: 0 }, "A deleted entry retained its old data");
    Require(ReferenceEquals(updated.Single(entry => entry.Node.Name == "b.bin").Node, after.Root.Children.Single()), "Map retained an obsolete node reference");
    Require(updated.Sum(entry => entry.Weight) == after.Root.Size, "Settled weights no longer match the current tree");
});

Console.WriteLine($"\n{passed} checks passed. Temporary fixture will be removed automatically.");
return;

void Write(string name, long length)
{
    string fullPath = Path.Combine(fixture, name);
    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
    using var stream = File.Create(fullPath);
    stream.SetLength(length);
}

async Task Check(string name, Func<Task> test)
{
    await test();
    passed++;
    Console.WriteLine("PASS  " + name);
}

static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message) { if (!condition) throw new Exception(message); }

static string Fingerprint(DiskNode root) => $"{root.Size}/{root.FileCount}/{root.DirectoryCount}:" + string.Join("|", root.Children.Select(n => $"{n.Name}/{n.Size}/{n.FileCount}/{n.DirectoryCount}/{n.WasEnumerated}/{n.IsDirectoryLink}"));

static void ValidateTree(DiskNode root)
{
    var stack = new Stack<DiskNode>(); stack.Push(root);
    while (stack.TryPop(out var node))
    {
        Require(node.Size == node.Children.Sum(x => x.Size), "Parent size differs from children");
        Require(node.FileCount == node.Children.Sum(x => x.FileCount), "Parent file count differs");
        long previous = long.MaxValue;
        foreach (var child in node.Children)
        {
            Require(child.Parent == node && File.Exists(child.FullPath) || child.Parent == node && Directory.Exists(child.FullPath), "Bad parent/path");
            Require(child.Size <= previous, "Children not sorted");
            previous = child.Size;
            if (child.IsDirectory) stack.Push(child);
        }
    }
}

static async Task Benchmark(string path)
{
    Console.WriteLine($"Read-only metadata benchmark: {Path.GetFullPath(path)}");
    Console.WriteLine("Timings include tree construction and sorting; cache/drive state affects results.");
    await new DiskScanner().ScanAsync(path, 4); // JIT and filesystem warmup
    foreach (int workers in new[] { 1, 2, 4, 8 })
    {
        var durations = new List<double>();
        long allocated = 0; ScanResult? last = null;
        for (int run = 0; run < 3; run++)
        {
            long before = GC.GetTotalAllocatedBytes(true);
            last = await new DiskScanner().ScanAsync(path, workers);
            allocated += GC.GetTotalAllocatedBytes(true) - before;
            durations.Add(last.Elapsed.TotalMilliseconds);
        }
        double median = durations.Order().ElementAt(1);
        Console.WriteLine($"{workers} workers: {median:N1} ms median · {last!.Root.FileCount:N0} files · {last.Root.FileCount / (median / 1000):N0} files/s · {SizeFormatter.Format(allocated / 3)} allocated/run · {last.Errors} unreadable");
    }
    var liveDurations = new List<double>();
    int snapshots = 0;
    var observer = new CallbackProgress(_ => Interlocked.Increment(ref snapshots));
    for (int run = 0; run < 3; run++)
    {
        var result = await new DiskScanner().ScanAsync(path, 4, observer);
        liveDurations.Add(result.Elapsed.TotalMilliseconds);
    }
    Console.WriteLine($"4 workers + live snapshots: {liveDurations.Order().ElementAt(1):N1} ms median · {snapshots} total snapshots in 3 runs");
}

static bool MarkSparse(FileStream stream) => NativeTest.DeviceIoControl(stream.SafeFileHandle, 0x900C4, null, 0, null, 0, out _, 0);

static void CreateJunction(string path, string target)
{
    Directory.CreateDirectory(path);
    using var handle = NativeTest.CreateFile(path, 0x40000000, 0, 0, 3, 0x02200000, 0);
    if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception();
    string substitute = @"\??\" + target;
    byte[] substituteBytes = System.Text.Encoding.Unicode.GetBytes(substitute);
    byte[] printBytes = System.Text.Encoding.Unicode.GetBytes(target);
    byte[] buffer = new byte[16 + substituteBytes.Length + 2 + printBytes.Length + 2];
    BitConverter.GetBytes(0xA0000003u).CopyTo(buffer, 0);
    BitConverter.GetBytes((ushort)(buffer.Length - 8)).CopyTo(buffer, 4);
    BitConverter.GetBytes((ushort)substituteBytes.Length).CopyTo(buffer, 10);
    BitConverter.GetBytes((ushort)(substituteBytes.Length + 2)).CopyTo(buffer, 12);
    BitConverter.GetBytes((ushort)printBytes.Length).CopyTo(buffer, 14);
    substituteBytes.CopyTo(buffer, 16);
    printBytes.CopyTo(buffer, 16 + substituteBytes.Length + 2);
    if (!NativeTest.DeviceIoControl(handle, 0x900A4, buffer, buffer.Length, null, 0, out _, 0)) throw new System.ComponentModel.Win32Exception();
}

}

internal static class NativeTest
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle file, uint code, byte[]? input, int inputSize, byte[]? output, int outputSize, out int returned, nint overlapped);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    public static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
}

internal sealed class CallbackProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
{
    public void Report(ScanProgress value) => report(value);
}
