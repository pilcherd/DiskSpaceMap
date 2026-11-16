namespace DiskSpaceMap.Core;

public sealed record ProgressiveMapEntry(string Key, DiskNode Node, double Weight);

/// <summary>Owns the visible identities for one scan. Completion replaces metadata,
/// never the ordering, grouping, or zero-weight slots that define the partitions.</summary>
public sealed class ProgressiveMap
{
    private readonly List<DiskNode> _slots = [];
    private string? _rootPath;

    public void Reset() { _slots.Clear(); _rootPath = null; }

    public IReadOnlyList<ProgressiveMapEntry> Update(DiskNode root, bool complete)
    {
        string path = root.FullPath;
        if (!StringComparer.OrdinalIgnoreCase.Equals(_rootPath, path)) Reset();
        _rootPath = path;
        if (_slots.Count == 0)
        {
            _slots.AddRange(root.Children.Take(512));
            if (root.Children.Count > 512)
                _slots[511] = new DiskNode("Other root items", root, false) { IsAggregate = true };
        }

        var visibleNames = _slots.Where(node => !node.IsAggregate).Select(node => node.Name).ToHashSet(StringComparer.Ordinal);
        var incoming = root.Children.Where(node => !node.IsAggregate && visibleNames.Contains(node.Name))
            .ToDictionary(node => node.Name, StringComparer.Ordinal);
        long visibleBytes = 0, visibleFiles = 0, visibleFolders = 0;
        for (int i = 0; i < _slots.Count; i++)
        {
            if (_slots[i].IsAggregate) continue;
            if (incoming.TryGetValue(_slots[i].Name, out var replacement)) _slots[i] = replacement;
            else if (complete)
            {
                // Retain the partition identity, but never retain deleted data.
                _slots[i] = new DiskNode(_slots[i].Name, root, _slots[i].IsDirectory) { WasEnumerated = true, FileCount = 0 };
                continue;
            }
            visibleBytes += _slots[i].Size;
            visibleFiles += _slots[i].FileCount;
            visibleFolders += _slots[i].DirectoryCount + (_slots[i].IsDirectory ? 1 : 0);
        }

        // A live overflow group stays a group after completion. Expanding it here
        // would change every binary split even though the bytes have barely changed.
        var aggregate = root.Children.FirstOrDefault(node => node.IsAggregate);
        for (int i = 0; i < _slots.Count; i++)
        {
            if (!_slots[i].IsAggregate) continue;
            _slots[i] = new DiskNode(_slots[i].Name, root, false)
            {
                IsAggregate = true,
                Size = Math.Max(0, root.Size - visibleBytes),
                FileCount = Math.Max(0, root.FileCount - visibleFiles),
                DirectoryCount = Math.Max(0, root.DirectoryCount - visibleFolders),
                WasEnumerated = complete || aggregate?.WasEnumerated == true,
                ScanError = aggregate?.ScanError
            };
        }

        double placeholder = Math.Max(1, (double)root.Size / Math.Max(1, _slots.Count) * 0.08);
        return _slots.Select(node =>
        {
            double weight = node.Size;
            if (!complete && (node.IsDirectory || node.IsAggregate) && !node.WasEnumerated
                && !node.IsDirectoryLink && node.ScanError == null)
                weight = Math.Max(weight, placeholder);
            return new ProgressiveMapEntry((node.IsAggregate ? "\0aggregate" : "") + node.Name, node, weight);
        }).ToArray();
    }
}
