using Microsoft.Extensions.Logging;
using RingMouse.Core;
using RingMouse.HidPlusPlus;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace RingMouse.App.Logging;

/// <summary>
/// Serilog: %APPDATA%\RingMouse\logs\ringmouse-JJJJMMTT.log (14 Tage) und bei Bedarf hidpp-JJJJMMTT.log
/// mit allen HID++-Rohframes (7 Tage). Keine Telemetrie – alles bleibt lokal.
/// </summary>
internal sealed class AppLogging : IDisposable
{
    private readonly LoggingLevelSwitch _level = new(LogEventLevel.Information);
    private readonly object _rawLock = new();
    private Serilog.Core.Logger? _raw;

    public AppLogging()
    {
        AppPaths.EnsureCreated();
        // Fehler des Loggers selbst (z.B. Datei nicht zu öffnen) ins Startprotokoll statt ins Nichts
        Serilog.Debugging.SelfLog.Enable(message => StartupTrace.Write("Serilog: " + message.TrimEnd()));
        StartupTrace.Write($"Logger → {AppPaths.LogDirectory}");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(_level)
            .Enrich.FromLogContext()
            .WriteTo.File(Path.Combine(AppPaths.LogDirectory, "ringmouse-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 20 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                flushToDiskInterval: TimeSpan.FromSeconds(2),
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        Factory = new SerilogLoggerFactory(Log.Logger, dispose: false);
        StartupTrace.Write($"Logger: level {_level.MinimumLevel}, Information enabled={Log.Logger.IsEnabled(LogEventLevel.Information)}");
    }

    /// <summary>Prüft, ob Einträge tatsächlich in der Datei landen (für das Startprotokoll).</summary>
    public void VerifyFileOutput()
    {
        var file = Path.Combine(AppPaths.LogDirectory, $"ringmouse-{DateTime.Now:yyyyMMdd}.log");
        var before = File.Exists(file) ? new FileInfo(file).Length : -1;
        Log.Logger.Information("Log started (PID {Pid})", Environment.ProcessId);
        var after = File.Exists(file) ? new FileInfo(file).Length : -1;
        StartupTrace.Write($"Log test: {file} {before} → {after} bytes ({(after > before ? "writing" : "NOT WRITING")})");
    }

    public ILoggerFactory Factory { get; }

    public Microsoft.Extensions.Logging.ILogger Create(string category) => Factory.CreateLogger(category);

    public void SetLevel(string? level)
    {
        _level.MinimumLevel = Enum.TryParse<LogEventLevel>(level, ignoreCase: true, out var parsed) ? parsed : LogEventLevel.Information;
    }

    public bool RawHidEnabled
    {
        get
        {
            lock (_rawLock) return _raw is not null;
        }
        set
        {
            lock (_rawLock)
            {
                if (value && _raw is null)
                {
                    _raw = new LoggerConfiguration()
                        .MinimumLevel.Verbose()
                        .WriteTo.File(Path.Combine(AppPaths.LogDirectory, "hidpp-.log"),
                            rollingInterval: RollingInterval.Day,
                            retainedFileCountLimit: 7,
                            fileSizeLimitBytes: 50 * 1024 * 1024,
                            rollOnFileSizeLimit: true,
                            flushToDiskInterval: TimeSpan.FromSeconds(2),
                            outputTemplate: "{Timestamp:HH:mm:ss.fff} {Message:l}{NewLine}")
                        .CreateLogger();
                    _raw.Information("--- Raw HID++ log started ---");
                }
                else if (!value && _raw is not null)
                {
                    _raw.Information("--- Raw HID++ log stopped ---");
                    _raw.Dispose();
                    _raw = null;
                }
            }
        }
    }

    public void WriteRaw(HidppChannel channel, FrameDirection direction, byte[] frame, long timestamp)
    {
        var raw = _raw;
        raw?.Information("{Dir} {Channel} {Frame}", direction == FrameDirection.Tx ? "TX" : "RX", channel.Name, HidppMessage.ToHex(frame));
    }

    public void Dispose()
    {
        RawHidEnabled = false;
        Factory.Dispose();
        Log.CloseAndFlush();
    }
}
