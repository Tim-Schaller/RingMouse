using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RingMouse.Platform.Native;

namespace RingMouse.Platform.Windows;

/// <summary>Findet Fenster eines Prozesses und holt sie zuverlässig in den Vordergrund (für App-Hotkeys).</summary>
public static unsafe class WindowActivator
{
    private const uint GW_OWNER = 4;

    public sealed record WindowCandidate(IntPtr Handle, uint ProcessId, string Title, bool Visible, bool Minimized);

    /// <summary>Hauptfenster eines Prozesses (Name mit/ohne .exe), sichtbare bevorzugt.</summary>
    public static WindowCandidate? FindMainWindow(string processName)
    {
        var name = Path.GetFileNameWithoutExtension(processName.Trim());
        var pids = new HashSet<uint>();
        foreach (var p in Process.GetProcessesByName(name))
        {
            pids.Add((uint)p.Id);
            p.Dispose();
        }
        if (pids.Count == 0) return null;

        var found = new List<WindowCandidate>();
        var state = new EnumState(pids, found);
        var handle = GCHandle.Alloc(state);
        try
        {
            Win32.EnumWindows(&EnumTop, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return found
            .OrderByDescending(w => w.Visible)
            .ThenBy(w => w.Minimized)
            .ThenByDescending(w => w.Title.Length)
            .FirstOrDefault();
    }

    private sealed record EnumState(HashSet<uint> Pids, List<WindowCandidate> Found);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int EnumTop(IntPtr hwnd, IntPtr lParam)
    {
        if (GCHandle.FromIntPtr(lParam).Target is not EnumState s) return 0;
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        if (!s.Pids.Contains(pid)) return 1;
        if (Win32.GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return 1;
        var title = GetTitle(hwnd);
        if (title.Length == 0) return 1;
        s.Found.Add(new WindowCandidate(hwnd, pid, title, Win32.IsWindowVisible(hwnd), Win32.IsIconic(hwnd)));
        return 1;
    }

    public static string GetTitle(IntPtr hwnd)
    {
        var len = Win32.GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        var buffer = new char[len + 1];
        fixed (char* p = buffer)
        {
            var n = Win32.GetWindowText(hwnd, p, buffer.Length);
            return new string(p, 0, Math.Max(0, n));
        }
    }

    /// <summary>
    /// Aktiviert ein Fenster. Windows erlaubt SetForegroundWindow nur dem Vordergrundprozess; per
    /// AttachThreadInput teilt der Aufrufer kurz den Eingabezustand des aktuellen Vordergrundthreads.
    /// Der aufrufende Thread braucht eine Message-Queue.
    /// </summary>
    public static bool Activate(IntPtr hwnd, TimeSpan timeout)
    {
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) return false;
        if (Win32.IsIconic(hwnd)) Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
        else if (!Win32.IsWindowVisible(hwnd)) Win32.ShowWindow(hwnd, Win32.SW_SHOW);

        Win32.PeekMessage(out _, IntPtr.Zero, 0, 0, Win32.PM_NOREMOVE);
        var foreground = Win32.GetForegroundWindow();
        if (foreground == hwnd) return true;

        var fgThread = foreground == IntPtr.Zero ? 0 : Win32.GetWindowThreadProcessId(foreground, out _);
        var myThread = Win32.GetCurrentThreadId();
        var attached = fgThread != 0 && fgThread != myThread && Win32.AttachThreadInput(myThread, fgThread, true);
        try
        {
            Win32.BringWindowToTop(hwnd);
            Win32.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) Win32.AttachThreadInput(myThread, fgThread, false);
        }

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (Win32.GetForegroundWindow() == hwnd) return true;
            Thread.Sleep(10);
        }
        return Win32.GetForegroundWindow() == hwnd;
    }

    public static void Minimize(IntPtr hwnd) => Win32.ShowWindow(hwnd, Win32.SW_MINIMIZE);

    public static void Hide(IntPtr hwnd) => Win32.ShowWindow(hwnd, Win32.SW_HIDE);

    public static bool IsAlive(IntPtr hwnd) => hwnd != IntPtr.Zero && Win32.IsWindow(hwnd);
}
