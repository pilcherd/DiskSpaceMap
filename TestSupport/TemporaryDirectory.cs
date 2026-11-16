using System.IO;

namespace DiskSpaceMap.TestSupport;

// Test data lives outside the checkout and is removed on both success and failure.
internal sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("DiskSpaceMap-checks-").FullName;

    public void Dispose()
    {
        string root = System.IO.Path.GetFullPath(Path);
        string temp = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetTempPath());
        if (!string.Equals(System.IO.Path.GetDirectoryName(root), temp, StringComparison.OrdinalIgnoreCase)
            || !System.IO.Path.GetFileName(root).StartsWith("DiskSpaceMap-checks-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to clean an unexpected test directory.");
        if (!Directory.Exists(root)) return;

        // Explicitly unlink junctions without following them (fixtures include a loop).
        var pending = new Stack<(string Directory, bool Visited)>();
        pending.Push((root, false));
        while (pending.TryPop(out var item))
        {
            if (item.Visited) { Directory.Delete(item.Directory); continue; }
            pending.Push((item.Directory, true));
            foreach (var entry in new DirectoryInfo(item.Directory).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.Directory) == 0) entry.Delete();
                else if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) Directory.Delete(entry.FullName);
                else pending.Push((entry.FullName, false));
            }
        }
    }
}
