using System.Diagnostics;
using System.Threading;

namespace RingMouse.App;

public static class Program
{
    public const string MutexName = @"Local\RingMouse.SingleInstance";
    public const string ActivateEventName = @"Local\RingMouse.Activate";
    public const string ExitEventName = @"Local\RingMouse.Exit";

    /// <summary>So lange nach Abmelde-/Herunterfahr-Beginn warten, bevor ein Abbruch angenommen wird.</summary>
    private static readonly TimeSpan SessionEndGrace = TimeSpan.FromSeconds(20);

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--exit", StringComparer.OrdinalIgnoreCase)) return ExitRunningInstance();
        // Diagnose ohne Gerät – darf neben einer laufenden Instanz laufen
        if (args.Contains("--render-ui", StringComparer.OrdinalIgnoreCase)) return new RingMouseApplication(args).Run();

        Logging.StartupTrace.WriteEnvironment(args);
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirst);
        if (!isFirst)
        {
            Logging.StartupTrace.Write("second instance – exiting");
            // Zweite Instanz: bei einem Autostart (Anmeldung/Entsperren) still beenden, sonst die laufende Instanz
            // ihre Einstellungen öffnen lassen.
            if (!args.Contains("--autostart", StringComparer.OrdinalIgnoreCase) &&
                EventWaitHandle.TryOpenExisting(ActivateEventName, out var activate))
            {
                activate.Set();
                activate.Dispose();
            }
            return 0;
        }

        var app = new RingMouseApplication(args);
        var code = app.Run();
        if (app.EndedBySessionEnding) RestartIfSessionContinues(mutex);
        else if (app.RestartRequested) StartNewInstance(mutex, "--settings");
        GC.KeepAlive(mutex);
        return code;
    }

    /// <summary>Gewünschter Neustart (z.B. Sprachwechsel): Sperre freigeben und eine neue Instanz starten.</summary>
    private static void StartNewInstance(Mutex mutex, string arguments)
    {
        mutex.ReleaseMutex();
        mutex.Dispose();
        try
        {
            if (Environment.ProcessPath is { } exe)
                Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Logging.StartupTrace.Write($"Restart failed: {ex.Message}");
        }
    }

    /// <summary>
    /// "--exit": laufende Instanz sauber beenden (Tastenumleitungen zurücksetzen), z.B. vor einem Update.
    /// Exit-Code 0 = beendet bzw. lief nicht, 1 = reagiert nicht, 2 = kein Zugriff (andere Sitzung/anderes Konto).
    /// </summary>
    private static int ExitRunningInstance()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(ExitEventName, out var exit)) return 0;
            using (exit) exit.Set();
        }
        catch (UnauthorizedAccessException)
        {
            return 2;
        }

        // Beendet ist sie, wenn ihre Einzelinstanz-Sperre verschwindet.
        try
        {
            for (var i = 0; i < 80; i++)
            {
                if (!Mutex.TryOpenExisting(MutexName, out var running)) return 0;
                running.Dispose();
                Thread.Sleep(100);
            }
        }
        catch (UnauthorizedAccessException)
        {
            return 2;
        }
        return 1;
    }

    /// <summary>
    /// Beim Abmelden/Herunterfahren beendet Windows den Prozess in wenigen Sekunden. Läuft er danach noch, wurde das
    /// Abmelden abgebrochen (z.B. "Abbrechen" wegen ungespeicherter Daten einer anderen App) – dann neu starten,
    /// statt ohne RingMouse weiterzuarbeiten.
    /// </summary>
    private static void RestartIfSessionContinues(Mutex mutex)
    {
        // Die neue Instanz muss die Einzelinstanz-Sperre bekommen.
        mutex.ReleaseMutex();
        mutex.Dispose();
        Thread.Sleep(SessionEndGrace);
        try
        {
            if (Environment.ProcessPath is { } exe)
                Process.Start(new ProcessStartInfo(exe, "--autostart") { UseShellExecute = true })?.Dispose();
        }
        catch
        {
            // Sitzung endet doch – nichts zu tun
        }
    }
}
