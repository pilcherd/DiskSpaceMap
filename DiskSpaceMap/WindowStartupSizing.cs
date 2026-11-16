using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DiskSpaceMap;

internal static class WindowStartupSizing
{
    internal readonly record struct Placement(Rect PixelBounds, Size LogicalSize, Size MinimumSize);

    // Work area excludes the taskbar. Keep a margin around the entire window,
    // including its title bar, and never let a fixed minimum force it off-screen.
    internal static Placement Calculate(Rect workAreaPixels, double dpi, Size preferred, Size minimum)
    {
        double scale = dpi / 96;
        double width = Math.Max(1, Math.Floor(Math.Min(preferred.Width * scale, workAreaPixels.Width * 0.92)));
        double height = Math.Max(1, Math.Floor(Math.Min(preferred.Height * scale, workAreaPixels.Height * 0.92)));
        var bounds = new Rect(
            workAreaPixels.Left + Math.Floor((workAreaPixels.Width - width) / 2),
            workAreaPixels.Top + Math.Floor((workAreaPixels.Height - height) / 2), width, height);
        var logical = new Size(width / scale, height / scale);
        return new(bounds, logical, new Size(Math.Min(minimum.Width, logical.Width), Math.Min(minimum.Height, logical.Height)));
    }

    internal static void ApplySize(Window window, Placement placement)
    {
        window.MinWidth = placement.MinimumSize.Width;
        window.MinHeight = placement.MinimumSize.Height;
        window.Width = placement.LogicalSize.Width;
        window.Height = placement.LogicalSize.Height;
    }

    internal static void Fit(Window window)
    {
        // SourceInitialized runs after WPF has positioned the window on its startup
        // monitor, before it is shown. The HWND now has that monitor's effective DPI.
        nint handle = new WindowInteropHelper(window).Handle;
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        nint monitor = MonitorFromWindow(handle, 2); // MONITOR_DEFAULTTONEAREST
        if (!GetMonitorInfoW(monitor, ref info)) return;
        uint dpi = GetDpiForWindow(handle);
        if (dpi == 0) dpi = 96;
        var work = info.WorkArea;
        var placement = Calculate(new Rect(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top),
            dpi, new Size(window.Width, window.Height), new Size(window.MinWidth, window.MinHeight));
        ApplySize(window, placement);
        var bounds = placement.PixelBounds;
        // Use device coordinates for placement, including monitors with negative
        // origins or a different scale from the primary display.
        SetWindowPos(handle, 0, (int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height,
            0x0004 | 0x0010); // SWP_NOZORDER | SWP_NOACTIVATE
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Monitor, WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
