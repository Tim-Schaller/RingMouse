using Microsoft.Extensions.Logging;
using RingMouse.Core.Config;
using RingMouse.Device;
using RingMouse.HidPlusPlus;
using RingMouse.HidPlusPlus.Features;
using RingMouse.HidPlusPlus.Transport.Windows;

namespace RingMouse.Probe;

/// <summary>Lässt den DeviceService der App in der Konsole laufen (Meilenstein-2-Test: Reconnect, Standby, Watchdog).</summary>
internal static class WatchCommand
{
    public static async Task<int> RunAsync(ProbeOptions o, CancellationToken ct)
    {
        var logger = new ConsoleLogger(o.Verbose ? LogLevel.Debug : LogLevel.Information);
        await using var service = new DeviceService(new WinHidTransport(), new WinHidDeviceWatcher(), logger,
            new DeviceServiceOptions { SoftwareId = o.SoftwareId, RequestTimeout = TimeSpan.FromMilliseconds(o.TimeoutMs) });

        var divert = o.ControlIds.ToHashSet();
        service.UpdateConfiguration(new DeviceConfiguration
        {
            DivertControls = divert,
            RawXYControls = o.RawXY ? divert : new HashSet<ushort>(),
            DisableAnalytics = o.Takeover,
            ClearForeignDiversions = o.Takeover,
            SettingsFor = (_, _, _) => new DeviceSettings { Dpi = o.SetDpi },
        });

        var names = new Dictionary<string, string>();
        long lastRawPrint = 0;
        int rawDx = 0, rawDy = 0;

        service.DeviceChanged += s =>
        {
            names[s.Key] = s.Name;
            var battery = s.Battery is { } b ? $"{b.EffectivePercent?.ToString() ?? "?"} % {Commands.DescribeState(b.State)}" : "–";
            ConsoleOut.Line(s.State == DeviceState.Ready ? ConsoleColor.Green : ConsoleColor.Yellow,
                $"{Now} STATE {s.Name}: {s.StateText} · {s.Connection} · battery {battery} · DPI {s.Dpi?.ToString() ?? "–"} · " +
                $"diverted [{string.Join(",", s.DivertedControls.Select(ControlIds.Format))}]" +
                (s.FailedControls.Count > 0 ? $" · failed [{string.Join(",", s.FailedControls.Select(ControlIds.Format))}]" : ""));
        };
        service.DeviceRemoved += s => ConsoleOut.Line(ConsoleColor.Red, $"{Now} REMOVED {s.Name}");
        service.ButtonChanged += e =>
            ConsoleOut.Line(ConsoleColor.Cyan, $"{Now} BUTTON {ControlIds.Format(e.ControlId)} {ControlIds.GetName(e.ControlId)} " +
                                               $"{(e.IsDown ? "PRESSED" : "released")} ({names.GetValueOrDefault(e.DeviceKey, e.DeviceKey)})");
        service.RawXY += e =>
        {
            rawDx += e.Dx;
            rawDy += e.Dy;
            var now = Environment.TickCount64;
            if (now - lastRawPrint < 150) return;
            lastRawPrint = now;
            ConsoleOut.Line(ConsoleColor.Magenta, $"{Now} RAW-XY sum dx={rawDx} dy={rawDy}");
        };
        if (o.Raw)
            service.FrameTraced += (_, dir, frame, _) => ConsoleOut.Hint($"{Now} {(dir == FrameDirection.Tx ? "TX" : "RX")} {HidppMessage.ToHex(frame)}");

        ConsoleOut.Heading(divert.Count == 0
            ? "watch: DeviceService running read-only (no diversion) – Ctrl+C quits"
            : $"watch: DeviceService diverts [{string.Join(", ", divert.Select(ControlIds.Format))}] – Ctrl+C quits and resets");
        ConsoleOut.Hint("Test: mouse off/on, Bluetooth off/on, standby, lock/unlock – the messages show reconnect and reconfiguration.");
        OptionsPlusCheck.WarnIfRunning();

        service.Start();
        try
        {
            var duration = o.DurationSeconds is { } s ? TimeSpan.FromSeconds(s) : Timeout.InfiniteTimeSpan;
            await Task.Delay(duration, ct);
        }
        catch (OperationCanceledException)
        {
            // Strg+C
        }
        await service.StopAsync(TimeSpan.FromSeconds(3));
        return 0;
    }

    private static string Now => DateTime.Now.ToString("HH:mm:ss.fff");
}

/// <summary>Minimaler Konsolen-Logger für die Probe.</summary>
internal sealed class ConsoleLogger(LogLevel minimum) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var color = logLevel switch
        {
            >= LogLevel.Error => ConsoleColor.Red,
            LogLevel.Warning => ConsoleColor.Yellow,
            LogLevel.Information => ConsoleColor.Gray,
            _ => ConsoleColor.DarkGray,
        };
        var text = $"{DateTime.Now:HH:mm:ss.fff} [{logLevel.ToString()[..3].ToUpperInvariant()}] {formatter(state, exception)}";
        if (exception is not null) text += $" – {exception.GetType().Name}: {exception.Message}";
        ConsoleOut.Line(color, text);
    }
}
