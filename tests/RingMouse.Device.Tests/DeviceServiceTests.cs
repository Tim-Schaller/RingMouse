using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RingMouse.Core.Config;
using Xunit;

namespace RingMouse.Device.Tests;

/// <summary>DeviceService gegen die simulierte MX Vertical: Lebenszyklus, Umleitung, Reconnect, Watchdog, Schlaf.</summary>
public class DeviceServiceTests
{
    private const ushort DpiSwitch = 0x00FD;

    private static readonly DeviceServiceOptions FastOptions = new()
    {
        TickInterval = TimeSpan.FromMilliseconds(40),
        WatchdogInterval = TimeSpan.FromMilliseconds(400),
        RescanInterval = TimeSpan.FromMilliseconds(400),
        PingTimeout = TimeSpan.FromMilliseconds(250),
        RequestTimeout = TimeSpan.FromMilliseconds(400),
    };

    private sealed class Harness : IAsyncDisposable
    {
        public SimulatedMouse Mouse { get; } = new();
        public SimulatedTransport Transport { get; }
        public DeviceService Service { get; }
        public ConcurrentQueue<ButtonEvent> Buttons { get; } = new();
        public ConcurrentQueue<DeviceSnapshot> Snapshots { get; } = new();
        public ConcurrentQueue<DeviceSnapshot> Removed { get; } = new();
        public ListLogger Log { get; } = new();

        public Harness(int? dpi = null, bool rawXY = true)
        {
            Transport = new SimulatedTransport(Mouse);
            Service = new DeviceService(Transport, null, Log, FastOptions);
            Service.UpdateConfiguration(new DeviceConfiguration
            {
                DivertControls = new HashSet<ushort> { DpiSwitch },
                RawXYControls = rawXY ? new HashSet<ushort> { DpiSwitch } : new HashSet<ushort>(),
                DisableAnalytics = true,
                ClearForeignDiversions = true,
                SettingsFor = (_, _, _) => new DeviceSettings { Dpi = dpi },
            });
            Service.ButtonChanged += e => Buttons.Enqueue(e);
            Service.DeviceChanged += s => Snapshots.Enqueue(s);
            Service.DeviceRemoved += s => Removed.Enqueue(s);
        }

        public DeviceSnapshot? Last => Snapshots.LastOrDefault();

        public async Task StartReadyAsync()
        {
            Service.Start();
            await Until(() => Last?.State == DeviceState.Ready, "Gerät wird Ready");
        }

        public async ValueTask DisposeAsync() => await Service.StopAsync(TimeSpan.FromSeconds(1));
    }

    private static async Task Until(Func<bool> condition, string what, int timeoutMs = 4000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.Fail($"Timeout: {what}{Environment.NewLine}{ListLogger.Current?.Dump()}");
    }

    [Fact]
    public async Task Start_DivertsWithRawXY_ClearsForeignDiversions_DisablesAnalytics()
    {
        await using var h = new Harness();
        await h.StartReadyAsync();

        var snapshot = h.Last!;
        Assert.Equal("MX Vertical", snapshot.Name);
        Assert.Equal("1A2B3C4D", snapshot.UnitId);
        Assert.True(snapshot.Battery?.Percent == 20, h.Log.Dump());
        Assert.Contains(DpiSwitch, snapshot.DivertedControls);
        Assert.Empty(snapshot.FailedControls);

        Assert.True(h.Mouse.Get(DpiSwitch).Diverted);
        Assert.True(h.Mouse.Get(DpiSwitch).RawXY);
        Assert.False(h.Mouse.Get(0x0053).Diverted);   // von "Options+" umgeleitet → wieder nativ
        Assert.False(h.Mouse.Get(0x0056).Diverted);
        Assert.False(h.Mouse.Get(0x0050).Analytics);  // Analytics aus
    }

    [Fact]
    public async Task ButtonEvents_AreDiffedIntoDownAndUp()
    {
        await using var h = new Harness();
        await h.StartReadyAsync();

        h.Mouse.Press(DpiSwitch);
        await Until(() => h.Buttons.Count >= 1, "Down");
        h.Mouse.Press();
        await Until(() => h.Buttons.Count >= 2, "Up");

        var events = h.Buttons.ToArray();
        Assert.Equal((DpiSwitch, true), (events[0].ControlId, events[0].IsDown));
        Assert.Equal((DpiSwitch, false), (events[1].ControlId, events[1].IsDown));
    }

    [Fact]
    public async Task Reconnect_0x1D4B_ReappliesDiversion()
    {
        await using var h = new Harness();
        await h.StartReadyAsync();

        h.Mouse.Press(DpiSwitch);          // gedrückt während der Verbindung abbricht …
        await Until(() => h.Buttons.Count >= 1, "Down");
        h.Mouse.Reconnect();                // … Umleitung weg, Gerät meldet 0x1D4B
        Assert.False(h.Mouse.Get(DpiSwitch).Diverted);

        await Until(() => h.Mouse.Get(DpiSwitch).Diverted && h.Mouse.Get(DpiSwitch).RawXY, "Umleitung nach Reconnect wieder gesetzt");
        await Until(() => h.Buttons.Any(b => !b.IsDown), "Taste beim Reconnect automatisch losgelassen");
    }

    [Fact]
    public async Task Watchdog_RestoresDiversionOverwrittenByOtherSoftware()
    {
        await using var h = new Harness();
        await h.StartReadyAsync();

        h.Mouse.ForeignReset(DpiSwitch);
        await Until(() => h.Mouse.Get(DpiSwitch).Diverted, "Watchdog setzt Umleitung neu");
    }

    [Fact]
    public async Task Watchdog_CorrectsDpiDrift_AndDpiIsAppliedOnConfigure()
    {
        await using var h = new Harness(dpi: 1600);
        await h.StartReadyAsync();
        Assert.Equal(1600, h.Mouse.Dpi);

        h.Mouse.Dpi = 4000; // native DPI-Taste oder fremde Software
        await Until(() => h.Mouse.Dpi == 1600, "DPI wird wieder auf 1600 gesetzt");
    }

    [Fact]
    public async Task Sleep_MarksUnreachable_WakeUpReconfigures()
    {
        await using var h = new Harness();
        await h.StartReadyAsync();

        h.Mouse.Asleep = true;
        await Until(() => h.Last?.State == DeviceState.Unreachable, "Unreachable im Schlaf");
        Assert.Equal(20, h.Last!.Battery?.Percent); // letzter Stand bleibt

        h.Mouse.ForeignReset(DpiSwitch);            // Schlaf hat die Umleitung gekostet
        h.Mouse.Asleep = false;
        h.Mouse.Reconnect();
        await Until(() => h.Last?.State == DeviceState.Ready && h.Mouse.Get(DpiSwitch).Diverted, "nach dem Aufwachen wieder Ready + umgeleitet");
    }

    [Fact]
    public async Task BatteryEvent_UpdatesSnapshot()
    {
        await using var h = new Harness();
        await h.StartReadyAsync();

        h.Mouse.BatteryEvent(9);
        await Until(() => h.Last?.Battery?.Percent == 9, "Akku-Event 9 %");
        h.Mouse.BatteryEvent(40, status: 1);
        await Until(() => h.Last?.Battery?.IsCharging == true, "lädt");
    }

    [Fact]
    public async Task Unplug_RemovesDevice_ReturnReconfigures()
    {
        await using var h = new Harness();
        await h.StartReadyAsync();

        h.Transport.Present = false;
        h.Mouse.Unplug();
        await Until(() => !h.Removed.IsEmpty, "DeviceRemoved");

        h.Mouse.ForeignReset(DpiSwitch);
        h.Transport.Present = true;
        await Until(() => h.Mouse.Get(DpiSwitch).Diverted && h.Service.Devices.Count == 1, "nach Wiederkehr neu geöffnet und umgeleitet");
    }

    [Fact]
    public async Task ChannelClosingRightAtStart_IsReopenedLater()
    {
        // Regression: schloss der Kanal, bevor Closed abonniert war, blieb die tote Sitzung für immer stehen.
        await using var h = new Harness();
        h.Mouse.FailingStarts = 2;
        await h.StartReadyAsync();
        Assert.True(h.Mouse.Get(DpiSwitch).Diverted);
        Assert.Single(h.Service.Devices);
    }

    [Fact]
    public async Task UnexpectedWriteErrors_DoNotStopTheService()
    {
        // Regression: eine unerwartete Ausnahme aus dem Transport beendete die Hauptschleife dauerhaft.
        await using var h = new Harness();
        h.Mouse.FaultyWrites = 3;
        await h.StartReadyAsync();
        Assert.True(h.Mouse.Get(DpiSwitch).Diverted);

        h.Mouse.FaultyWrites = 2;          // auch im laufenden Betrieb (Watchdog)
        h.Mouse.ForeignReset(DpiSwitch);
        await Until(() => h.Mouse.Get(DpiSwitch).Diverted && h.Last?.State == DeviceState.Ready, "erholt sich und leitet wieder um");
    }

    [Fact]
    public async Task HangingEnumeration_IsSkipped_DeviceStillComesUp()
    {
        // Treiber beantwortet die Enumeration nicht: Scan wird übersprungen statt die Schleife zu blockieren
        var transport = new SimulatedTransport(new SimulatedMouse()) { EnumerateDelayMs = 1500 };
        var log = new ListLogger();
        var snapshots = new ConcurrentQueue<DeviceSnapshot>();
        await using var service = new DeviceService(transport, null, log,
            new DeviceServiceOptions
            {
                TickInterval = TimeSpan.FromMilliseconds(40), RescanInterval = TimeSpan.FromMilliseconds(300),
                RequestTimeout = TimeSpan.FromMilliseconds(400), PingTimeout = TimeSpan.FromMilliseconds(250),
                EnumerationTimeout = TimeSpan.FromMilliseconds(300),
            });
        service.UpdateConfiguration(new DeviceConfiguration { DivertControls = new HashSet<ushort> { DpiSwitch } });
        service.DeviceChanged += s => snapshots.Enqueue(s);
        service.Start();

        await Until(() => log.Dump().Contains("HID-Enumeration hängt"), "Zeitlimit der Enumeration greift");
        transport.EnumerateDelayMs = 0;
        await Until(() => snapshots.LastOrDefault()?.State == DeviceState.Ready, "Gerät kommt trotzdem hoch", 6000);
    }

    [Fact]
    public async Task Watchdog_CancelsStalledIteration()
    {
        // Durchlauf wartet (abbrechbar) auf eine lange Enumeration: der Wächter bricht ihn ab, danach geht es weiter
        var transport = new SimulatedTransport(new SimulatedMouse()) { EnumerateDelayMs = 2500 };
        var log = new ListLogger();
        var snapshots = new ConcurrentQueue<DeviceSnapshot>();
        await using var service = new DeviceService(transport, null, log,
            new DeviceServiceOptions
            {
                TickInterval = TimeSpan.FromMilliseconds(40), RescanInterval = TimeSpan.FromMilliseconds(300),
                RequestTimeout = TimeSpan.FromMilliseconds(400), PingTimeout = TimeSpan.FromMilliseconds(250),
                EnumerationTimeout = TimeSpan.FromSeconds(30), LoopStallTimeout = TimeSpan.FromMilliseconds(600),
            });
        service.UpdateConfiguration(new DeviceConfiguration { DivertControls = new HashSet<ushort> { DpiSwitch } });
        service.DeviceChanged += s => snapshots.Enqueue(s);
        service.Start();

        await Until(() => log.Dump().Contains("Geräteverwaltung hängt"), "Wächter erkennt den Stillstand", 5000);
        await Until(() => log.Dump().Contains("Durchlauf abgebrochen"), "Durchlauf wird abgebrochen", 5000);
        transport.EnumerateDelayMs = 0;
        await Until(() => snapshots.LastOrDefault()?.State == DeviceState.Ready, "danach normal weiter", 8000);
    }

    [Fact]
    public async Task Stop_UndivertsOwnDiversions()
    {
        var h = new Harness();
        await h.StartReadyAsync();
        Assert.True(h.Mouse.Get(DpiSwitch).Diverted);

        await h.Service.StopAsync(TimeSpan.FromSeconds(1));
        Assert.False(h.Mouse.Get(DpiSwitch).Diverted);
        Assert.False(h.Mouse.Get(DpiSwitch).RawXY);
    }

    [Fact]
    public async Task ConfigChange_UndivertsControlsNoLongerBound()
    {
        await using var h = new Harness();
        await h.StartReadyAsync();

        h.Service.UpdateConfiguration(new DeviceConfiguration
        {
            DivertControls = new HashSet<ushort> { 0x0053 },
            DisableAnalytics = true,
            ClearForeignDiversions = true,
        });
        await Until(() => h.Mouse.Get(0x0053).Diverted && !h.Mouse.Get(DpiSwitch).Diverted, "neue Belegung angewendet");
    }
}


internal sealed class ListLogger : ILogger
{
    private readonly ConcurrentQueue<string> _lines = new();

    public ListLogger() => Current = this;

    public static ListLogger? Current { get => s_current; private set => s_current = value; }
    private static ListLogger? s_current;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        _lines.Enqueue($"{DateTime.Now:HH:mm:ss.fff} [{logLevel}] {formatter(state, exception)}{(exception is null ? "" : " " + exception)}");

    public string Dump() => string.Join(Environment.NewLine, _lines);
}
