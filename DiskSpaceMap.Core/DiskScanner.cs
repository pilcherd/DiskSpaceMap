using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Channels;

namespace DiskSpaceMap.Core;

/// <summary>A fixed worker pool enumerates directories; no task, stat call or full path per file.</summary>
public sealed class DiskScanner
{
    private sealed class LiveBranch(DiskNode node)
    {
        public DiskNode Node { get; } = node;
        public long Bytes, Files, Directories, Pending, Errors;
    }

    private readonly record struct WorkItem(DiskNode Directory, LiveBranch? Branch);

    public async Task<ScanResult> ScanAsync(string path, int workerCount = 0,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Scanning requires Windows.");
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException($"Folder is unavailable: {fullPath}");
        workerCount = workerCount == 0 ? Math.Clamp(Environment.ProcessorCount, 2, 4) : Math.Clamp(workerCount, 1, 16);
        // Do not inspect/traverse a selected junction either: it could escape the selected drive.
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Select the target folder directly instead of a directory link or junction.");

        var root = new DiskNode(fullPath, null, true);
        var queue = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions { SingleWriter = false, SingleReader = workerCount == 1, AllowSynchronousContinuations = false });
        var warnings = new ConcurrentQueue<string>();
        // Only the root worker writes this list, before the other workers/reporter start.
        // A fixed cap keeps both snapshot allocation and UI work independent of drive size.
        var branches = new List<LiveBranch>();
        LiveBranch? overflow = null;
        var rootReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long pending = 1, files = 0, directories = 0, bytes = 0, errors = 0, links = 0;
        string currentPath = fullPath;
        var clock = Stopwatch.StartNew();
        queue.Writer.TryWrite(new(root, null));

        using var reporterStop = new CancellationTokenSource();
        // Prime just the drive root first, then publish all top-level placeholders immediately.
        // Descendant work retains its top-level branch; byte batches need no ancestor walks.
        var firstWorker = Task.Run(WorkerAsync);
        await rootReady.Task.ConfigureAwait(false);
        var reporter = ReportAsync();
        var workers = new[] { firstWorker }.Concat(Enumerable.Range(1, workerCount - 1).Select(_ => Task.Run(WorkerAsync))).ToArray();
        try { await Task.WhenAll(workers).ConfigureAwait(false); }
        finally
        {
            reporterStop.Cancel();
            await reporter.ConfigureAwait(false);
        }
        PublishProgress();

        // Iterative post-order reduction handles arbitrarily deep directory trees.
        await Task.Run(() =>
        {
            var stack = new Stack<(DiskNode Node, bool Visited)>();
            stack.Push((root, false));
            while (stack.TryPop(out var item))
            {
                if (item.Visited) { item.Node.AggregateAndSort(); continue; }
                stack.Push((item.Node, true));
                foreach (var child in item.Node.Children)
                    if (child.IsDirectory) stack.Push((child, false));
            }
        }).ConfigureAwait(false);
        clock.Stop();
        return new(root, clock.Elapsed, errors, links, cancellationToken.IsCancellationRequested, workerCount, warnings.ToArray());

        async Task WorkerAsync()
        {
            try
            {
                while (await queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (queue.Reader.TryRead(out var work))
                    {
                        var directory = work.Directory;
                        var branch = work.Branch;
                        long localFiles = 0, localBytes = 0, localLinks = 0;
                        try
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            string directoryPath = directory.FullPath;
                            Volatile.Write(ref currentPath, directoryPath);
                            NativeDirectory.Enumerate(directoryPath, entry =>
                            {
                                var node = new DiskNode(entry.Name, directory, entry.IsDirectory, entry.IsDirectory ? 0 : entry.Size);
                                directory.Add(node);
                                LiveBranch? childBranch = branch;
                                if (directory == root && progress != null)
                                {
                                    if (branches.Count < 511)
                                    {
                                        childBranch = new LiveBranch(node);
                                        branches.Add(childBranch);
                                    }
                                    else
                                    {
                                        if (overflow == null)
                                        {
                                            overflow = new LiveBranch(new DiskNode("Other root items", root, false) { IsAggregate = true });
                                            branches.Add(overflow);
                                        }
                                        childBranch = overflow;
                                    }
                                    if (!entry.IsDirectory)
                                    {
                                        childBranch.Files++;
                                        childBranch.Bytes += entry.Size;
                                    }
                                    else if (childBranch == overflow) childBranch.Directories++;
                                }
                                if (entry.IsDirectory)
                                {
                                    Interlocked.Increment(ref directories);
                                    if (branch != null) Interlocked.Increment(ref branch.Directories);
                                    if (entry.IsReparsePoint) { node.IsDirectoryLink = true; localLinks++; }
                                    else
                                    {
                                        if (childBranch != null) Interlocked.Increment(ref childBranch.Pending);
                                        Interlocked.Increment(ref pending);
                                        queue.Writer.TryWrite(new(node, childBranch));
                                    }
                                }
                                else { localFiles++; localBytes += entry.Size; }
                                // Publish in batches so even one huge directory has live progress.
                                if (localFiles >= 256)
                                {
                                    Interlocked.Add(ref files, localFiles);
                                    Interlocked.Add(ref bytes, localBytes);
                                    if (branch != null)
                                    {
                                        Interlocked.Add(ref branch.Files, localFiles);
                                        Interlocked.Add(ref branch.Bytes, localBytes);
                                    }
                                    localFiles = localBytes = 0;
                                }
                            }, cancellationToken);
                            directory.WasEnumerated = true;
                        }
                        catch (Win32Exception ex)
                        {
                            directory.ScanError = ex.Message;
                            if (branch != null) Interlocked.Increment(ref branch.Errors);
                            if (Interlocked.Increment(ref errors) <= 100) warnings.Enqueue($"{directory.FullPath}: {ex.Message}");
                        }
                        finally
                        {
                            Interlocked.Add(ref files, localFiles);
                            Interlocked.Add(ref bytes, localBytes);
                            if (branch != null)
                            {
                                Interlocked.Add(ref branch.Files, localFiles);
                                Interlocked.Add(ref branch.Bytes, localBytes);
                                Interlocked.Decrement(ref branch.Pending);
                            }
                            Interlocked.Add(ref links, localLinks);
                            if (Interlocked.Decrement(ref pending) == 0) queue.Writer.TryComplete();
                            if (directory == root)
                            {
                                PublishProgress();
                                rootReady.TrySetResult();
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                // Release other workers if an unexpected failure makes the queue incomplete.
                queue.Writer.TryComplete(ex);
                throw;
            }
            finally { rootReady.TrySetResult(); }
        }

        async Task ReportAsync()
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(150));
                while (await timer.WaitForNextTickAsync(reporterStop.Token).ConfigureAwait(false))
                    PublishProgress();
            }
            catch (OperationCanceledException) when (reporterStop.IsCancellationRequested) { }
        }

        void PublishProgress()
        {
            if (progress == null) return;
            // Never expose the mutable scan tree to the dispatcher. Each tiny snapshot is
            // owned by its recipient and remains unchanged after publication.
            var snapshot = new DiskNode(fullPath, null, true);
            foreach (var live in branches)
            {
                var source = live.Node;
                var child = new DiskNode(source.Name, snapshot, source.IsDirectory)
                {
                    Size = Interlocked.Read(ref live.Bytes),
                    FileCount = Interlocked.Read(ref live.Files),
                    DirectoryCount = Interlocked.Read(ref live.Directories),
                    WasEnumerated = Interlocked.Read(ref live.Pending) == 0,
                    IsDirectoryLink = source.IsDirectoryLink,
                    IsAggregate = source.IsAggregate,
                    ScanError = Interlocked.Read(ref live.Errors) > 0 ? "Some folders could not be read" : source.ScanError
                };
                snapshot.Add(child);
                snapshot.Size += child.Size;
                snapshot.FileCount += child.FileCount;
                snapshot.DirectoryCount += child.DirectoryCount + (child.IsDirectory ? 1 : 0);
            }
            progress.Report(new(Interlocked.Read(ref files), Interlocked.Read(ref directories),
                Interlocked.Read(ref bytes), Interlocked.Read(ref errors), Interlocked.Read(ref links),
                clock.Elapsed, Volatile.Read(ref currentPath), snapshot));
        }
    }
}
