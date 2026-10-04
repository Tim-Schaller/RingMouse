using Microsoft.Extensions.Logging;
using RingMouse.App.Tray;
using RingMouse.Core.Battery;
using RingMouse.Core.Config;
using RingMouse.Core.State;
using RingMouse.Device;
using static RingMouse.Core.Localization.Lang;

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

        var percent = battery.EffectivePercent;
        BatteryAlert? alert = null;
        // Zustand unter dem StateStore-Lock ändern – sonst kann store.Save() das Batteries-Dictionary
        // während der Serialisierung verändert sehen ("Collection was modified").
        store.Mutate(state =>
        {
            if (!state.Batteries.TryGetValue(device.Key, out var saved))
            {
                saved = new DeviceBatteryState();
                state.Batteries[device.Key] = saved;
            }
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
            alert = tracker.Update(percent, battery.IsCharging || (battery.ExternalPower && !battery.IsFull), battery.IsFull);
        });

        var name = TrayController.ShortName(device.Name);
        switch (alert)
        {
            case BatteryAlert.Low low:
                log.LogInformation("{Device}: battery {Percent} % – threshold {Threshold} % reached", device.Name, low.Percent, low.Threshold);
                tray.Notify(L($"{name}: battery {low.Percent} %", $"{name}: Akku {low.Percent} %"),
                    low.Threshold <= 5
                        ? L("Please charge now – the mouse will turn off soon.", "Bitte jetzt aufladen – die Maus schaltet sich bald ab.")
                        : L("Please charge soon.", "Bitte bald aufladen."),
                    low.Threshold <= 10 ? TrayNotice.Error : TrayNotice.Warning);
                break;
            case BatteryAlert.Charged when _config.Battery.NotifyCharged:
                log.LogInformation("{Device}: charging complete", device.Name);
                tray.Notify(L($"{name}: charging complete", $"{name}: Aufladen abgeschlossen"), L("The battery is full.", "Der Akku ist voll."),
                    TrayNotice.Info);
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
