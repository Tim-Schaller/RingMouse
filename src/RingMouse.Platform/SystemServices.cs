using System.Diagnostics;
using System.Runtime.InteropServices;
using RingMouse.Platform.Native;

namespace RingMouse.Platform;

/// <summary>Erkennt laufende Logi-Options+-Komponenten (überschreiben Tastenumleitungen).</summary>
public static class OptionsPlusDetector
{
    private static readonly string[] s_processNames =
        ["logioptionsplus_agent", "logioptionsplus", "LogiOptionsMgr", "LogiOptions", "LogiPluginService"];

    public static IReadOnlyList<string> RunningProcesses()
    {
        var found = new List<string>();
        foreach (var name in s_processNames)
        {
            var procs = Process.GetProcessesByName(name);
            if (procs.Length > 0) found.Add(name);
            foreach (var p in procs) p.Dispose();
        }
        return found;
    }
}

/// <summary>Systemfunktionen ohne Tastaturkürzel (Win+L lässt sich nicht per SendInput auslösen).</summary>
public static class SystemCommands
{
    private const int SC_MONITORPOWER = 0xF170;

    public static bool LockWorkstation() => Win32.LockWorkStation();

    public static void MonitorOff() =>
        Win32.PostMessage(Win32.HWND_BROADCAST, Win32.WM_SYSCOMMAND, SC_MONITORPOWER, 2);
}

/// <summary>Zugeordnetes Icon einer Datei/Exe als HICON (Aufrufer gibt es mit <see cref="Destroy"/> frei).</summary>
public static unsafe partial class ShellIcons
{
    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;

    [StructLayout(LayoutKind.Sequential)]
    private struct SHFILEINFOW
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        public fixed char szDisplayName[260];
        public fixed char szTypeName[80];
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHGetFileInfoW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, SHFILEINFOW* psfi, uint cbFileInfo, uint uFlags);

    public static IntPtr GetAssociatedIcon(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (!Path.IsPathRooted(expanded))
        {
            var resolved = ResolveOnPath(expanded);
            if (resolved is null) return IntPtr.Zero;
            expanded = resolved;
        }
        SHFILEINFOW info;
        var result = SHGetFileInfo(expanded, 0, &info, (uint)sizeof(SHFILEINFOW), SHGFI_ICON | SHGFI_LARGEICON);
        return result == IntPtr.Zero ? IntPtr.Zero : info.hIcon;
    }

    public static void Destroy(IntPtr hIcon)
    {
        if (hIcon != IntPtr.Zero) Win32.DestroyIcon(hIcon);
    }

    private static string? ResolveOnPath(string file)
    {
        var candidates = new List<string> { Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.SystemDirectory };
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries));
        foreach (var dir in candidates)
        {
            try
            {
                var full = Path.Combine(dir.Trim(), file);
                if (File.Exists(full)) return full;
                if (!Path.HasExtension(full) && File.Exists(full + ".exe")) return full + ".exe";
            }
            catch
            {
                // ungültige PATH-Einträge ignorieren
            }
        }
        return null;
    }
}
