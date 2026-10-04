using System.Windows;
using System.Windows.Threading;
using RingMouse.App.Diagnostics;
using Serilog;

namespace RingMouse.App;

/// <summary>WPF-Anwendung ohne Hauptfenster (Tray-App). Fluent-Theme folgt Hell/Dunkel von Windows.</summary>
internal sealed class RingMouseApplication : Application
{
    private readonly string[] _args;
    private AppHost? _host;

    public RingMouseApplication(string[] args)
    {
        _args = args;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            ThemeMode = ThemeMode.System;
        }
        catch
        {
            // Fluent-Theme nicht verfügbar → Standard-Theme
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            RingMouse.Platform.Display.CursorHider.Show(); // nie mit ausgeblendetem Mauszeiger abstürzen
            Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception (terminating={Terminating})", args.IsTerminating);
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RingMouse.Platform.Display.CursorHider.Show();
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        var renderIndex = Array.FindIndex(_args, a => a.Equals("--render-ui", StringComparison.OrdinalIgnoreCase));
        if (renderIndex >= 0)
        {
            var dir = renderIndex + 1 < _args.Length ? _args[renderIndex + 1] : Path.Combine(Path.GetTempPath(), "ringmouse-ui");
            UiSnapshots.RenderAll(dir);
            Shutdown();
            return;
        }

        _host = new AppHost(this, _args);
        _host.Start();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled exception on the UI thread");
        e.Handled = true; // weiterlaufen – ein Fehler im Ring darf die Tastenumleitung nicht beenden
    }

    /// <summary>Beendet, weil Windows die Sitzung beendet (Abmelden/Herunterfahren) – siehe Program.Main.</summary>
    public bool EndedBySessionEnding { get; private set; }

    /// <summary>Beendet, weil ein Neustart gewünscht ist (z.B. Sprachwechsel) – siehe Program.Main.</summary>
    public bool RestartRequested => _host?.RestartRequested == true;

    /// <summary>Argumente der neu gestarteten Instanz (z.B. leer nach einem Update, sonst "--settings").</summary>
    public string RestartArguments => _host?.RestartArguments ?? "--settings";

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if (e.Cancel) return;
        EndedBySessionEnding = true;
        _host?.ShutdownBlocking();
        // WPF beendet danach ohnehin; explizit, damit nie ein Prozess ohne Tray und Funktion übrig bleibt.
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.ShutdownBlocking();
        base.OnExit(e);
    }
}
