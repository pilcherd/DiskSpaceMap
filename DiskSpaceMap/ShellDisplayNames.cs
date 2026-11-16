using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using DiskSpaceMap.Core;

namespace DiskSpaceMap;

// Presentation only: never replace a node's identity or its filesystem path.
internal static class ShellDisplayNames
{
    private sealed record Lookup(string Path, string Fallback, TaskCompletionSource<string> Completion);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Task<string>> Cache = new(StringComparer.Ordinal);
    private static readonly Queue<string> Age = new();
    private static readonly BlockingCollection<Lookup> Pending = new(1024);
    private static int _revision;

    static ShellDisplayNames()
    {
        var worker = new Thread(ResolvePending) { IsBackground = true, Name = "Shell display names" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    internal static int Revision => Volatile.Read(ref _revision);

    internal static string Get(DiskNode node)
    {
        // Preserve extensions, root paths, and synthetic group labels.
        if (!node.IsDirectory || node.Parent == null || node.IsAggregate) return node.Name;
        var lookup = GetDirectoryNameAsync(node.FullPath, node.Name);
        return lookup.IsCompletedSuccessfully ? lookup.Result : node.Name;
    }

    internal static Task<string> GetDirectoryNameAsync(string path, string fallback)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(path, out var cached)) return cached;
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!Pending.TryAdd(new(path, fallback, completion))) return Task.FromResult(fallback);
            // Bound both queued work and retained results even when browsing a huge drive.
            if (Cache.Count >= 8192) Cache.Remove(Age.Dequeue());
            Age.Enqueue(path);
            Cache.Add(path, completion.Task);
            return completion.Task;
        }
    }

    private static void ResolvePending()
    {
        int initialized = CoInitializeEx(0, 2);
        try
        {
            foreach (var lookup in Pending.GetConsumingEnumerable())
            {
                string name = lookup.Fallback;
                if (initialized >= 0)
                {
                    try { name = Resolve(lookup.Path) ?? name; }
                    catch (ExternalException) { /* Keep the actual name if Shell metadata is unavailable. */ }
                }
                lookup.Completion.SetResult(name);
                if (name != lookup.Fallback) Interlocked.Increment(ref _revision);
            }
        }
        finally { if (initialized >= 0) CoUninitialize(); }
    }

    private static string? Resolve(string path)
    {
        const uint displayName = 0x200, pidlFlag = 0x8;
        uint size = (uint)Marshal.SizeOf<ShellFileInfo>();
        // Each drive's physical storage folder represents the same localized Shell object.
        string? parent = Path.GetDirectoryName(path);
        bool recycleBin = Path.GetFileName(path).Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
            && parent != null && Path.TrimEndingDirectorySeparator(parent).Equals(
                Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path) ?? ""), StringComparison.OrdinalIgnoreCase);
        if (recycleBin)
        {
            var id = new Guid("B7534046-3ECB-4C18-BE4E-64CD4CB7D6AC");
            nint pidl = 0;
            try
            {
                if (SHGetKnownFolderIDList(ref id, 0, 0, out pidl) >= 0 && pidl != 0
                    && GetFileInfoById(pidl, 0, out var info, size, displayName | pidlFlag) != 0
                    && !string.IsNullOrWhiteSpace(info.DisplayName)) return info.DisplayName;
            }
            finally { if (pidl != 0) Marshal.FreeCoTaskMem(pidl); }
        }
        if (path.Length < 260 && GetFileInfoByPath(path, 0, out var result, size, displayName) != 0
            && !string.IsNullOrWhiteSpace(result.DisplayName)) return result.DisplayName;
        return null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public nint Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nuint GetFileInfoByPath(string path, uint attributes, out ShellFileInfo info, uint size, uint flags);
    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", ExactSpelling = true)]
    private static extern nuint GetFileInfoById(nint pidl, uint attributes, out ShellFileInfo info, uint size, uint flags);
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderIDList(ref Guid id, uint flags, nint token, out nint pidl);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();
}
