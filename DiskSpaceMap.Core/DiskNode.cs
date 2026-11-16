namespace DiskSpaceMap.Core;

/// <summary>Only names and parent links are retained; full paths are built on demand.</summary>
public sealed class DiskNode
{
    private readonly List<DiskNode>? _children;
    public string Name { get; private set; }
    public DiskNode? Parent { get; private set; }
    public bool IsDirectory { get; }
    public bool IsDirectoryLink { get; internal set; }
    public bool IsAggregate { get; internal set; }
    public string? ScanError { get; internal set; }
    public bool WasEnumerated { get; internal set; }
    public long Size { get; internal set; }
    public long FileCount { get; internal set; }
    public long DirectoryCount { get; internal set; }
    public IReadOnlyList<DiskNode> Children => _children ?? (IReadOnlyList<DiskNode>)Array.Empty<DiskNode>();

    internal DiskNode(string name, DiskNode? parent, bool isDirectory, long size = 0)
    {
        Name = name;
        Parent = parent;
        IsDirectory = isDirectory;
        Size = size;
        FileCount = isDirectory ? 0 : 1;
        if (isDirectory) _children = [];
    }

    public string FullPath
    {
        get
        {
            if (IsAggregate && Parent != null) return Parent.FullPath;
            var parts = new Stack<string>();
            for (DiskNode? node = this; node != null; node = node.Parent) parts.Push(node.Name);
            return Path.Combine(parts.ToArray());
        }
    }

    internal void Add(DiskNode child) => _children!.Add(child);

    /// <summary>Replaces this folder with a fresh scan of the same location.
    /// Unrelated branches are retained and ancestor totals are recalculated.</summary>
    public DiskNode ReplaceWith(DiskNode refreshed)
    {
        ArgumentNullException.ThrowIfNull(refreshed);
        if (ReferenceEquals(this, refreshed)) return this;
        if (!IsDirectory || !refreshed.IsDirectory || refreshed.Parent != null
            || !StringComparer.Ordinal.Equals(FullPath, refreshed.FullPath))
            throw new ArgumentException("A refresh must be a standalone scan of the same folder.", nameof(refreshed));
        if (Parent is { } parent)
        {
            int index = parent._children!.IndexOf(this);
            if (index < 0) throw new InvalidOperationException("The folder is no longer in this scan tree.");
            refreshed.Name = Name;
            refreshed.Parent = parent;
            parent._children[index] = refreshed;
            for (var ancestor = parent; ancestor != null; ancestor = ancestor.Parent)
                ancestor.AggregateAndSort();
        }
        return refreshed;
    }

    /// <summary>Promotes a node from a shallow progress snapshot to the equivalent
    /// node in this complete scan tree, without reading the file system again.</summary>
    public DiskNode? ResolveSnapshotNode(DiskNode candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.IsAggregate) return null;
        var names = new Stack<string>();
        var top = candidate;
        while (top.Parent != null) { names.Push(top.Name); top = top.Parent; }
        // Even a node whose old parent chain reaches this root may have been
        // replaced by a scoped refresh. Always resolve against current children.
        if (!StringComparer.OrdinalIgnoreCase.Equals(top.Name, Name)) return null;
        DiskNode? resolved = this;
        while (names.TryPop(out string? name))
        {
            resolved = resolved.Children.FirstOrDefault(child => StringComparer.Ordinal.Equals(child.Name, name));
            if (resolved == null) return null;
        }
        return resolved;
    }

    internal void AggregateAndSort()
    {
        if (_children is null) return;
        Size = FileCount = DirectoryCount = 0;
        foreach (var child in _children)
        {
            Size += child.Size;
            FileCount += child.FileCount;
            DirectoryCount += child.DirectoryCount + (child.IsDirectory ? 1 : 0);
        }
        _children.Sort(static (a, b) =>
        {
            int sizeOrder = b.Size.CompareTo(a.Size);
            return sizeOrder != 0 ? sizeOrder : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        });
        _children.TrimExcess();
    }
}

public sealed record ScanProgress(long Files, long Directories, long Bytes, long Errors,
    long SkippedLinks, TimeSpan Elapsed, string CurrentPath, DiskNode? MapRoot = null);

public sealed record ScanResult(DiskNode Root, TimeSpan Elapsed, long Errors,
    long SkippedLinks, bool WasCancelled, int WorkerCount, IReadOnlyList<string> Warnings);

public static class SizeFormatter
{
    public static string Format(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return unit == 0 ? $"{bytes:N0} B" : $"{size:0.##} {units[unit]}";
    }
}
