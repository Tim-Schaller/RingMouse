using System.Runtime.InteropServices;
using RingMouse.Platform.Native;

namespace RingMouse.Platform.Display;

/// <summary>
/// Blendet den Mauszeiger systemweit aus, indem alle Systemzeiger kurzzeitig durch einen leeren ersetzt werden
/// (Windows kennt kein "Zeiger aus" für fremde Fenster), und lädt danach das Zeigerschema des Benutzers neu.
/// Eine Markierungsdatei sorgt dafür, dass ein beim Ausblenden hart beendeter Prozess den Zeiger beim nächsten
/// Start zurückgibt. Nur vom UI-Thread verwenden.
/// </summary>
public static partial class CursorHider
{
    private const uint SPI_SETCURSORS = 0x0057;

    // OCR_NORMAL, IBEAM, WAIT, CROSS, UP, SIZENWSE, SIZENESW, SIZEWE, SIZENS, SIZEALL, NO, HAND, APPSTARTING, HELP, PIN, PERSON
    private static readonly uint[] s_systemCursors =
        [32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650, 32651, 32671, 32672];

    private static string? s_markerPath;

    public static bool IsHidden { get; private set; }

    /// <summary>Beim Start aufrufen: war der Zeiger bei einem Absturz noch ausgeblendet, jetzt wiederherstellen.</summary>
    public static bool Initialize(string markerPath)
    {
        s_markerPath = markerPath;
        if (!File.Exists(markerPath)) return false;
        Restore();
        return true;
    }

    public static void Hide()
    {
        if (IsHidden) return;
        IsHidden = true;
        try
        {
            if (s_markerPath is not null) File.WriteAllText(s_markerPath, DateTime.Now.ToString("O"));
        }
        catch
        {
            // ohne Markierung weiter – Wiederherstellen klappt im Normalfall trotzdem
        }
        foreach (var id in s_systemCursors)
        {
            var blank = CreateBlankCursor();
            if (blank != IntPtr.Zero && !SetSystemCursor(blank, id)) DestroyCursor(blank); // Erfolg: System übernimmt das Handle
        }
    }

    public static void Show()
    {
        if (!IsHidden) return;
        IsHidden = false;
        Restore();
    }

    private static void Restore()
    {
        SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0);
        try
        {
            if (s_markerPath is not null && File.Exists(s_markerPath)) File.Delete(s_markerPath);
        }
        catch
        {
            // egal
        }
    }

    private static unsafe IntPtr CreateBlankCursor()
    {
        const int size = 32;
        var and = stackalloc byte[size * size / 8];
        var xor = stackalloc byte[size * size / 8];
        new Span<byte>(and, size * size / 8).Fill(0xFF); // AND = 1: Bildschirm bleibt, XOR = 0: nichts gezeichnet
        new Span<byte>(xor, size * size / 8).Clear();
        return CreateCursor(Win32.GetModuleHandle(null), 0, 0, size, size, and, xor);
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static unsafe partial IntPtr CreateCursor(IntPtr hInst, int xHotSpot, int yHotSpot, int nWidth, int nHeight, byte* pvAndPlane, byte* pvXorPlane);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetSystemCursor(IntPtr hcur, uint id);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyCursor(IntPtr hCursor);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);
}
