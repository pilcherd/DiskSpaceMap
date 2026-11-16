using DiskSpaceMap.Core;

namespace DiskSpaceMap;

/// <summary>The single owner of the selected folder and its breadcrumb path.
/// All navigation resolves into the completed scan tree; preview nodes stay read-only.</summary>
internal sealed class FolderNavigator
{
    private DiskNode? _root;
    public DiskNode? Current { get; private set; }
    public IReadOnlyList<DiskNode> Breadcrumbs { get; private set; } = [];
    public event Action<DiskNode>? Changed;

    public void BeginScan()
    {
        _root = Current = null;
        Breadcrumbs = [];
    }

    public void Preview(DiskNode snapshot)
    {
        if (_root != null) return;
        Current = snapshot;
        Breadcrumbs = [snapshot];
    }

    public void Complete(DiskNode root, DiskNode? currentFolder = null)
    {
        _root = root;
        Open(currentFolder ?? root);
    }

    public bool Open(DiskNode candidate)
    {
        var folder = _root?.ResolveSnapshotNode(candidate);
        if (folder is not { IsDirectory: true, IsDirectoryLink: false }) return false;
        Current = folder;
        var ancestors = new Stack<DiskNode>();
        for (var node = folder; node != null; node = node.Parent) ancestors.Push(node);
        Breadcrumbs = ancestors.ToArray();
        Changed?.Invoke(folder);
        return true;
    }

    public bool Up() => Current?.Parent is { } parent && Open(parent);
    public bool Root() => _root != null && Open(_root);
}
