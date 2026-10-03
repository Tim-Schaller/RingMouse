using Microsoft.Win32;
using RingMouse.Platform.Native;

namespace RingMouse.Platform.Display;

/// <summary>Hell/Dunkel-Einstellung und Akzentfarbe von Windows.</summary>
public static class SystemTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>App-Modus (Fenster, Ring).</summary>
    public static bool AppsUseLightTheme => ReadDword(PersonalizeKey, "AppsUseLightTheme", 1) != 0;

    /// <summary>Windows-Modus (Taskleiste, Infobereich) – wichtig für die Tray-Icon-Farbe.</summary>
    public static bool SystemUsesLightTheme => ReadDword(PersonalizeKey, "SystemUsesLightTheme", 0) != 0;

    /// <summary>Akzentfarbe als (R, G, B).</summary>
    public static (byte R, byte G, byte B) AccentColor
    {
        get
        {
            // HKCU\Software\Microsoft\Windows\DWM\AccentColor ist ABGR
            var abgr = ReadDword(@"Software\Microsoft\Windows\DWM", "AccentColor", -1);
            if (abgr != -1)
            {
                var v = unchecked((uint)abgr);
                return ((byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF));
            }
            if (Win32.DwmGetColorizationColor(out var argb, out _) == 0)
                return ((byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));
            return (0x00, 0x78, 0xD4);
        }
    }

    private static int ReadDword(string path, string name, int fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            return key?.GetValue(name) is int i ? i : fallback;
        }
        catch
        {
            return fallback;
        }
    }
}
