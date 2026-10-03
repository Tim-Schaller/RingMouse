using RingMouse.Core.Ring;
using RingMouse.Platform.Native;

namespace RingMouse.Platform.Display;

/// <summary>Monitor mit Bereichen in physischen Pixeln (Prozess ist Per-Monitor-DPI-aware v2).</summary>
public readonly record struct MonitorInfo(IntPtr Handle, Bounds Bounds, Bounds WorkArea, uint Dpi)
{
    public double Scale => Dpi / 96.0;
}

public static class Monitors
{
    public static MonitorInfo FromPoint(int x, int y)
    {
        var handle = Win32.MonitorFromPoint(new Win32.POINT { X = x, Y = y }, Win32.MONITOR_DEFAULTTONEAREST);
        var mi = new Win32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
        Win32.GetMonitorInfo(handle, ref mi);
        uint dpi = 96;
        if (Win32.GetDpiForMonitor(handle, Win32.MDT_EFFECTIVE_DPI, out var dx, out _) == 0 && dx > 0) dpi = dx;
        return new MonitorInfo(handle, ToBounds(mi.rcMonitor), ToBounds(mi.rcWork), dpi);
    }

    public static (int X, int Y) CursorPosition()
    {
        Win32.GetCursorPos(out var p);
        return (p.X, p.Y);
    }

    public static void SetCursorPosition(int x, int y) => Win32.SetCursorPos(x, y);

    private static Bounds ToBounds(Win32.RECT r) => new(r.Left, r.Top, r.Right, r.Bottom);
}
