using System.Runtime.InteropServices;
using RingMouse.Platform.Native;

namespace RingMouse.Platform.Clipboard;

/// <summary>Inhalt der Zwischenablage (alle HGLOBAL-Formate) zum späteren Wiederherstellen.</summary>
public sealed class ClipboardSnapshot
{
    internal List<(uint Format, byte[] Data)> Items { get; } = [];
    public int FormatCount => Items.Count;
    public bool IsEmpty => Items.Count == 0;
}

/// <summary>
/// Win32-Zwischenablage mit Sicherung/Wiederherstellung. Eingefügte Texte werden vom Zwischenablage-Verlauf
/// (Win+V) und der Cloud-Synchronisierung ausgenommen. Braucht ein Fenster-Handle des aufrufenden Threads.
/// </summary>
public sealed unsafe class ClipboardService : IDisposable
{
    private const long MaxSnapshotBytes = 64L * 1024 * 1024;
    private static readonly uint[] s_gdiFormats = [2 /*BITMAP*/, 3 /*METAFILEPICT*/, 9 /*PALETTE*/, 14 /*ENHMETAFILE*/,
        0x80 /*OWNERDISPLAY*/, 0x82 /*DSPBITMAP*/, 0x83 /*DSPMETAFILEPICT*/, 0x8E /*DSPENHMETAFILE*/];

    private readonly IntPtr _window;
    private readonly uint _excludeMonitor;
    private readonly uint _canIncludeInHistory;
    private readonly uint _canUploadToCloud;

    public ClipboardService()
    {
        // Message-only-Fenster als Besitzer (OpenClipboard(NULL) + EmptyClipboard lässt SetClipboardData scheitern)
        _window = Win32.CreateWindowEx(0, "STATIC", "RingMouse Clipboard", 0, 0, 0, 0, 0, Win32.HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        _excludeMonitor = Win32.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
        _canIncludeInHistory = Win32.RegisterClipboardFormat("CanIncludeInClipboardHistory");
        _canUploadToCloud = Win32.RegisterClipboardFormat("CanUploadToCloudClipboard");
    }

    public string? GetText()
    {
        if (!Open()) return null;
        try
        {
            var h = Win32.GetClipboardData(Win32.CF_UNICODETEXT);
            if (h == IntPtr.Zero) return null;
            var size = (long)Win32.GlobalSize(h);
            var p = Win32.GlobalLock(h);
            if (p == IntPtr.Zero) return null;
            try
            {
                // Nicht blind bis zum NUL lesen: fremde Apps legen CF_UNICODETEXT auch ohne Terminator ab.
                var text = new ReadOnlySpan<char>((char*)p, (int)Math.Min(size / sizeof(char), int.MaxValue));
                var end = text.IndexOf('\0');
                return new string(end >= 0 ? text[..end] : text);
            }
            finally
            {
                Win32.GlobalUnlock(h);
            }
        }
        finally
        {
            Win32.CloseClipboard();
        }
    }

    public ClipboardSnapshot Snapshot()
    {
        var snapshot = new ClipboardSnapshot();
        if (!Open()) return snapshot;
        try
        {
            long total = 0;
            uint format = 0;
            while ((format = Win32.EnumClipboardFormats(format)) != 0)
            {
                if (s_gdiFormats.Contains(format)) continue;
                var h = Win32.GetClipboardData(format);
                if (h == IntPtr.Zero) continue;
                var size = (long)Win32.GlobalSize(h);
                if (size <= 0 || total + size > MaxSnapshotBytes) continue;
                var p = Win32.GlobalLock(h);
                if (p == IntPtr.Zero) continue;
                try
                {
                    var data = new byte[size];
                    Marshal.Copy(p, data, 0, (int)size);
                    snapshot.Items.Add((format, data));
                    total += size;
                }
                finally
                {
                    Win32.GlobalUnlock(h);
                }
            }
        }
        finally
        {
            Win32.CloseClipboard();
        }
        return snapshot;
    }

    public bool SetText(string text)
    {
        if (!Open()) return false;
        try
        {
            Win32.EmptyClipboard();
            var bytes = new byte[(text.Length + 1) * 2];
            fixed (char* s = text)
            {
                Marshal.Copy((IntPtr)s, bytes, 0, text.Length * 2);
            }
            var ok = SetData(Win32.CF_UNICODETEXT, bytes);
            MarkPrivate();
            return ok;
        }
        finally
        {
            Win32.CloseClipboard();
        }
    }

    public void Restore(ClipboardSnapshot snapshot)
    {
        if (!Open()) return;
        try
        {
            Win32.EmptyClipboard();
            foreach (var (format, data) in snapshot.Items)
            {
                if (format == _excludeMonitor || format == _canIncludeInHistory || format == _canUploadToCloud) continue;
                SetData(format, data);
            }
            MarkPrivate(); // kein zweiter Eintrag im Verlauf
        }
        finally
        {
            Win32.CloseClipboard();
        }
    }

    private void MarkPrivate()
    {
        SetData(_excludeMonitor, [0]);
        SetData(_canIncludeInHistory, BitConverter.GetBytes(0));
        SetData(_canUploadToCloud, BitConverter.GetBytes(0));
    }

    private static bool SetData(uint format, byte[] data)
    {
        if (format == 0) return false;
        var h = Win32.GlobalAlloc(Win32.GMEM_MOVEABLE, (nuint)Math.Max(1, data.Length));
        if (h == IntPtr.Zero) return false;
        var p = Win32.GlobalLock(h);
        if (p == IntPtr.Zero)
        {
            Win32.GlobalFree(h);
            return false;
        }
        Marshal.Copy(data, 0, p, data.Length);
        Win32.GlobalUnlock(h);
        if (Win32.SetClipboardData(format, h) != IntPtr.Zero) return true; // System besitzt den Speicher jetzt
        Win32.GlobalFree(h);
        return false;
    }

    private bool Open()
    {
        for (var i = 0; i < 20; i++)
        {
            if (Win32.OpenClipboard(_window)) return true;
            Thread.Sleep(15); // andere App hält die Zwischenablage gerade
        }
        return false;
    }

    public void Dispose()
    {
        if (_window != IntPtr.Zero) Win32.DestroyWindow(_window);
    }
}
