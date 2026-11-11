using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace HanekawaPet.Platform;

public static class DesktopPlatform
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExToolWindow = 0x80;
    private const int WsExNoActivate = 0x08000000;

    public static void SetClickThrough(Window window, bool enabled)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() | WsExToolWindow;
        style = enabled ? style | WsExTransparent | WsExNoActivate : style & ~(WsExTransparent | WsExNoActivate);
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style));
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
    }

    public static bool IsClickThrough(Window window) =>
        (GetWindowLongPtr(new WindowInteropHelper(window).Handle, GwlExStyle).ToInt64() & WsExTransparent) != 0;

    public static Rect WorkArea(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rectangle)) return SystemParameters.WorkArea;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info)) return SystemParameters.WorkArea;
        var dpi = VisualTreeHelper.GetDpi(window);
        return new Rect(window.Left + (info.Work.Left - rectangle.Left) / dpi.DpiScaleX,
            window.Top + (info.Work.Top - rectangle.Top) / dpi.DpiScaleY,
            (info.Work.Right - info.Work.Left) / dpi.DpiScaleX,
            (info.Work.Bottom - info.Work.Top) / dpi.DpiScaleY);
    }

    public static void Clamp(Window window)
    {
        var area = WorkArea(window);
        window.Left = Math.Clamp(window.Left, area.Left, Math.Max(area.Left, area.Right - window.Width));
        window.Top = Math.Clamp(window.Top, area.Top, Math.Max(area.Top, area.Bottom - window.Height));
    }

    public static void ParkAtCursor(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        GetCursorPos(out var cursor);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromPoint(cursor, 2), ref info)) return;
        var dpi = VisualTreeHelper.GetDpi(window);
        var x = info.Work.Right - (int)((window.Width + 28) * dpi.DpiScaleX);
        var y = info.Work.Bottom - (int)((window.Height + 10) * dpi.DpiScaleY);
        SetWindowPos(hwnd, IntPtr.Zero, Math.Max(info.Work.Left, x), Math.Max(info.Work.Top, y), 0, 0, 0x0001 | 0x0004 | 0x0010);
        Clamp(window);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
}
