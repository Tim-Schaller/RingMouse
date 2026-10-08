using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using RingMouse.Core.Autostart;

namespace RingMouse.Platform.Windows;

/// <summary>
/// Verknüpfung "RingMouse" im Startmenü des Benutzers (%APPDATA%\…\Start Menu\Programs, ohne Adminrechte), damit sich
/// RingMouse auch ohne Autostart finden und starten lässt. Läuft es schon, öffnet derselbe Start die Einstellungen.
/// </summary>
public static class StartMenuShortcut
{
    public static string LinkPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "RingMouse.lnk");

    /// <summary>Exe, auf die die Verknüpfung zeigt; null = keine Verknüpfung oder nicht lesbar.</summary>
    public static string? ReadTarget()
    {
        if (!File.Exists(LinkPath)) return null;
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(LinkPath, 0);
            var target = new StringBuilder(1024);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            return target.Length == 0 ? null : Environment.ExpandEnvironmentVariables(target.ToString());
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    /// <summary>
    /// Legt die Verknüpfung auf <paramref name="exePath"/> an, wenn sie fehlt oder ins Leere zeigt (z.B. nach einem
    /// Umzug der Exe). Eine Verknüpfung auf eine andere vorhandene Kopie bleibt unangetastet. true = geändert.
    /// </summary>
    public static bool Ensure(string exePath)
    {
        if (File.Exists(LinkPath) && AutostartTarget.Classify(ReadTarget(), exePath, File.Exists) != AutostartTargetState.Missing)
            return false;
        Write(exePath);
        return true;
    }

    /// <summary>Entfernt die Verknüpfung, wenn sie auf <paramref name="exePath"/> zeigt oder ins Leere. true = entfernt.</summary>
    public static bool Remove(string exePath)
    {
        if (!File.Exists(LinkPath)) return false;
        if (AutostartTarget.Classify(ReadTarget(), exePath, File.Exists) == AutostartTargetState.OtherCopy) return false;
        File.Delete(LinkPath);
        return true;
    }

    private static void Write(string exePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LinkPath)!);
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(exePath);
            link.SetWorkingDirectory(Path.GetDirectoryName(exePath) ?? "");
            link.SetIconLocation(exePath, 0);
            link.SetDescription("RingMouse");
            ((IPersistFile)link).Save(LinkPath, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
