using System.Runtime.InteropServices;

namespace RingMouse.Platform.Update;

/// <summary>Zeit seit der letzten Benutzereingabe (Maus/Tastatur), damit Updates nur im Leerlauf installiert werden.</summary>
public static partial class SystemIdle
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LastInputInfo info);

    /// <summary>Sekunden seit der letzten Eingabe; 0, wenn es sich nicht ermitteln lässt.</summary>
    public static double Seconds()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return 0;
        // GetTickCount und info.Time teilen dieselbe Basis; die Differenz ist auch über den 49-Tage-Überlauf korrekt (unsigned).
        var elapsedMs = unchecked((uint)Environment.TickCount - info.Time);
        return elapsedMs / 1000.0;
    }
}
