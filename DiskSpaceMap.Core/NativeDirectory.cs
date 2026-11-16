using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DiskSpaceMap.Core;

internal static partial class NativeDirectory
{
    // Blittable buffers avoid allocating/marshalling two strings for every entry.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct FindData
    {
        public uint Attributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint SizeHigh, SizeLow, Reserved0, Reserved1;
        public fixed char FileName[260];
        public fixed char AlternateFileName[14];
    }

    internal readonly record struct Entry(string Name, bool IsDirectory, bool IsReparsePoint, long Size);

    internal static unsafe void Enumerate(string path, Action<Entry> visit, CancellationToken cancellation)
    {
        string extended = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
            : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..]
            : @"\\?\" + path;
        string pattern = extended.TrimEnd('\\') + @"\*";
        FindData data;
        nint handle = FindFirstFileEx(pattern, 1 /* FindExInfoBasic */, out data, 0, 0, 2 /* LARGE_FETCH */);
        if (handle == -1 && Marshal.GetLastPInvokeError() is 87 or 50)
            handle = FindFirstFileEx(pattern, 1, out data, 0, 0, 0);
        if (handle == -1)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == 2) return; // Empty directory on file systems that omit . and ..
            throw new Win32Exception(error);
        }
        try
        {
            do
            {
                cancellation.ThrowIfCancellationRequested();
                string name = new(data.FileName);
                if (name is "." or "..") continue;
                visit(new Entry(name, (data.Attributes & 0x10) != 0,
                    (data.Attributes & 0x400) != 0, ((long)data.SizeHigh << 32) | data.SizeLow));
            } while (FindNextFile(handle, out data));
            int error = Marshal.GetLastPInvokeError();
            if (error != 18) throw new Win32Exception(error); // ERROR_NO_MORE_FILES
        }
        finally { FindClose(handle); }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindFirstFileEx(string name, int infoLevel, out FindData data, int searchOp, nint filter, int flags);
    [LibraryImport("kernel32.dll", EntryPoint = "FindNextFileW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FindNextFile(nint handle, out FindData data);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FindClose(nint handle);
}
