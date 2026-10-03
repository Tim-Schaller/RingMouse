using Microsoft.Extensions.Logging;
using RingMouse.App.Tray;
using RingMouse.Core.Battery;
using RingMouse.Core.Config;
using RingMouse.Core.State;
using RingMouse.Device;

namespace RingMouse.App.Battery;

/// <summary>
/// Akku-Meldungen je Gerät (Schwellen einmal pro Entladezyklus, "Aufladen abgeschlossen") und Persistenz
/// des letzten bekannten Stands, damit er auch angezeigt wird, während das Gerät schläft.
/// </summary>
internal sealed class BatteryNotifier(TrayController tray, StateStore store, ILogger log)
{
    private readonly Dictionary<string, BatteryAlertTracker> _trackers = new();
    private RingMouseConfig _config = DefaultConfig.Create();
    private DateTime _lastSave = DateTime.MinValue;

    public void Update(RingMouseConfig config)
    {
        _config = config;
        _trackers.Clear(); // neue Schwellen → Tracker neu, Zustand bleibt in state.json
    }

    /// <summary>Muss auf dem UI-Thread aufgerufen werden.</summary>
    public void OnDevice(DeviceSnapshot device)
    {
        if (device.Battery is not { } battery) return;

        if (!store.State.Batteries.TryGetValue(device.Key, out var saved))
        {
            saved = new DeviceBatteryState();
            store.State.Batteries[device.Key] = saved;
        }
        var percent = battery.EffectivePercent;
        saved.DeviceName = device.Name;
        if (percent is not null)
        {
            // Ohne Wert (z.B. MX Vertical beim Laden) den letzten bekannten Stand behalten
            saved.Percent = percent;
            saved.Timestamp = device.BatteryTimestamp ?? DateTimeOffset.Now;
        }
        saved.State = TrayController.ChargeText(battery.State);
        saved.Connection = device.Connection;

        if (!_trackers.TryGetValue(device.Key, out var tracker))
        {
            tracker = new BatteryAlertTracker(_config.Battery.Thresholds, saved.Alerts);
            _trackers[device.Key] = tracker;
        }

        var alert = tracker.Update(percent, battery.IsCharging || (battery.ExternalPower && !battery.IsFull), battery.IsFull);
        var name = TrayController.ShortName(device.Name);
        switch (alert)
        {
            case BatteryAlert.Low low:
                log.LogInformation("{Device}: Akku {Percent} % – Schwelle {Threshold} % erreicht", device.Name, low.Percent, low.Threshold);
                tray.Notify($"{name}: Akku {low.Percent} %",
                    low.Threshold <= 5 ? "Bitte jetzt aufladen – die Maus schaltet sich bald ab." : "Bitte bald aufladen.",
                    low.Threshold <= 10 ? TrayNotice.Error : TrayNotice.Warning);
                break;
            case BatteryAlert.Charged when _config.Battery.NotifyCharged:
                log.LogInformation("{Device}: Aufladen abgeschlossen", device.Name);
                tray.Notify($"{name}: Aufladen abgeschlossen", "Der Akku ist voll.", TrayNotice.Info);
                break;
        }

        if (alert is not null || DateTime.UtcNow - _lastSave > TimeSpan.FromMinutes(5))
        {
            store.Save();
            _lastSave = DateTime.UtcNow;
        }
    }

    public DeviceBatteryState? LastKnown(string deviceKey) => store.State.Batteries.GetValueOrDefault(deviceKey);

    /// <summary>Letzter gespeicherter Stand eines Geräts per Name (für die Anzeige vor der ersten Verbindung).</summary>
    public DeviceBatteryState? LastKnownAny(string? nameFilter) =>
        store.State.Batteries.Values
            .Where(b => DeviceMatching.MatchesTrayFilter(nameFilter, b.DeviceName))
            .OrderByDescending(b => b.Timestamp)
            .FirstOrDefault();
}
