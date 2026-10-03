using System.Diagnostics;

namespace RingMouse.App.Logging;

/// <summary>
/// Kleines Startprotokoll unter %LOCALAPPDATA%\RingMouse\startup.log – unabhängig von Serilog und der Config,
/// damit auch Autostart-Probleme (andere Umgebung, Logger ohne Ausgabe) nachvollziehbar sind. Bleibt klein.
/// </summary>
internal static class StartupTrace
{
    private static readonly string s_path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RingMouse", "startup.log");
    private static readonly object s_lock = new();

    public static string FilePath => s_path;

    public static void Write(string message)
    {
        try
        {
            lock (s_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(s_path)!);
                var info = new FileInfo(s_path);
                if (info.Exists && info.Length > 256 * 1024) File.Delete(s_path);
                File.AppendAllText(s_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Startprotokoll darf nie stören
        }
    }

    public static void WriteEnvironment(string[] args)
    {
        using var self = Process.GetCurrentProcess();
        Write($"Start \"{string.Join(' ', args)}\" · {Environment.UserDomainName}\\{Environment.UserName} · session {self.SessionId} · " +
              $"priority {self.PriorityClass} · APPDATA={Environment.GetEnvironmentVariable("APPDATA")} · " +
              $"AppData(API)={Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)} · TEMP={Path.GetTempPath()} · " +
              $"RINGMOUSE_HOME={Environment.GetEnvironmentVariable("RINGMOUSE_HOME") ?? "–"} · CWD={Environment.CurrentDirectory}");
    }
}
