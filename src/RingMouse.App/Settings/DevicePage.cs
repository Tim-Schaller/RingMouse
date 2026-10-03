using System.Windows;
using System.Windows.Controls;
using RingMouse.App.Tray;
using RingMouse.Core.Config;
using RingMouse.Device;
using RingMouse.HidPlusPlus.Features;
using static RingMouse.Core.Localization.Lang;

namespace RingMouse.App.Settings;

/// <summary>Verbundene Geräte (Status, Akku, DPI) und Akku-Benachrichtigungen.</summary>
internal sealed class DevicePage : UserControl
{
    private readonly SettingsContext _ctx;
    private readonly ListBox _devices = new() { MinHeight = 120 };
    private readonly StackPanel _details = new();
    private IReadOnlyList<DeviceSnapshot> _snapshots = [];
    private string? _selectedKey;

    public DevicePage(SettingsContext ctx)
    {
        _ctx = ctx;
        var stack = new StackPanel { Margin = new Thickness(12) };
        stack.Children.Add(Form.Heading(L("Devices", "Geräte")));
        stack.Children.Add(_devices);
        stack.Children.Add(Form.Buttons(Form.Button(L("Reconnect", "Neu verbinden"), () => _ctx.Host.ReconnectAll())));
        stack.Children.Add(_details);
        stack.Children.Add(BuildBattery());
        Content = Form.Scroll(stack);

        _devices.SelectionChanged += (_, _) =>
        {
            _selectedKey = _devices.SelectedIndex >= 0 && _devices.SelectedIndex < _snapshots.Count ? _snapshots[_devices.SelectedIndex].Key : null;
            BuildDetails();
        };
        Refresh();
    }

    public void Refresh()
    {
        _snapshots = _ctx.Host.Devices.OrderBy(d => d.Name).ToList();
        var keep = _selectedKey;
        _devices.Items.Clear();
        foreach (var d in _snapshots)
        {
            var battery = d.Battery is { } b ? $"{b.EffectivePercent?.ToString() ?? "?"} % ({TrayController.ChargeText(b.State)})" : "–";
            _devices.Items.Add(L($"{d.Name} · {d.Connection} · {d.StateText} · battery {battery} · DPI {d.Dpi?.ToString() ?? "–"}",
                $"{d.Name} · {d.Connection} · {d.StateText} · Akku {battery} · DPI {d.Dpi?.ToString() ?? "–"}"));
        }
        if (_snapshots.Count == 0)
            _devices.Items.Add(L("No Logitech HID++ device found (is the mouse turned on and connected?)",
                "Kein Logitech-HID++-Gerät gefunden (Maus eingeschaltet und verbunden?)"));
        var index = _snapshots.ToList().FindIndex(d => d.Key == keep);
        _devices.SelectedIndex = index >= 0 ? index : _snapshots.Count > 0 ? 0 : -1;
    }

    private void BuildDetails()
    {
        _details.Children.Clear();
        var d = _snapshots.FirstOrDefault(s => s.Key == _selectedKey);
        if (d is null) return;

        var box = new StackPanel();
        box.Children.Add(Form.Row("Name", new TextBlock { Text = d.Name, FontWeight = FontWeights.SemiBold }));
        box.Children.Add(Form.Row(L("Connection", "Verbindung"), new TextBlock { Text = $"{d.Connection}, HID++ {d.Protocol?.ToString() ?? "?"}, PID {d.ProductId:X4}" }));
        box.Children.Add(Form.Row(L("Unit ID", "Unit-ID"), new TextBlock { Text = d.UnitId ?? "–" }));
        box.Children.Add(Form.Row("Status", new TextBlock { Text = d.StateText }));
        box.Children.Add(Form.Row(L("Diverted", "Umgeleitet"), new TextBlock
        {
            Text = d.DivertedControls.Count == 0 ? "–" : string.Join(", ", d.DivertedControls.Select(c => $"{ControlIds.Format(c)} {ControlIds.GetName(c)}")),
            TextWrapping = TextWrapping.Wrap,
        }));
        if (d.FailedControls.Count > 0)
            box.Children.Add(Form.Row(L("Not divertable", "Nicht umleitbar"), new TextBlock { Text = string.Join(", ", d.FailedControls.Select(ControlIds.Format)) }));
        if (d.Battery is { } b)
        {
            box.Children.Add(Form.Row(L("Battery", "Akku"), new TextBlock
            {
                Text = $"{b.EffectivePercent?.ToString() ?? "?"} % · {TrayController.ChargeText(b.State)}{(b.VoltageMillivolts is { } mv ? $" · {mv} mV" : "")}" +
                       $" · {L("source", "Quelle")} {b.Source}{(d.BatteryTimestamp is { } t ? $" · {t:HH:mm:ss}" : "")}",
            }));
        }

        if (d.SupportedDpi.Count > 0)
        {
            var key = DeviceKey(d);
            var settings = _ctx.Config.Devices.GetValueOrDefault(key);
            var choices = new List<(int? Value, string Text)> { (null, L("don't change", "nicht ändern")) };
            choices.AddRange(d.SupportedDpi.Where(v => v % 100 == 0 || d.SupportedDpi.Count < 40).Select(v => ((int?)v, $"{v} DPI")));
            var dpiBox = Form.Choice(choices, settings?.Dpi, v =>
            {
                if (!_ctx.Config.Devices.TryGetValue(key, out var s))
                {
                    s = new DeviceSettings();
                    _ctx.Config.Devices[key] = s;
                }
                s.Dpi = v;
                _ctx.MarkDirty();
            });
            var apply = Form.Button(L("Set now", "Jetzt setzen"), async () =>
            {
                var value = _ctx.Config.Devices.GetValueOrDefault(key)?.Dpi;
                if (value is { } dpi) await _ctx.Host.SetDpiNowAsync(d.Key, dpi);
            });
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(dpiBox);
            row.Children.Add(new Border { Width = 8 });
            row.Children.Add(apply);
            box.Children.Add(Form.Row(L("DPI (persistent)", "DPI (dauerhaft)"), row,
                L($"Currently {d.Dpi?.ToString() ?? "?"} DPI. Re-applied after every reconnect (config key \"{key}\").",
                    $"Aktuell {d.Dpi?.ToString() ?? "?"} DPI. Wird nach jedem Reconnect erneut gesetzt (Config-Schlüssel \"{key}\").")));
        }

        _details.Children.Add(Form.Group(L("Selected device", "Ausgewähltes Gerät"), box));
    }

    private string DeviceKey(DeviceSnapshot d)
    {
        var existing = _ctx.Config.Devices.Keys.FirstOrDefault(k => k != "*" && DeviceMatching.Matches(k, d.Name, d.ProductId, d.UnitId));
        return existing ?? TrayController.ShortName(d.Name);
    }

    private FrameworkElement BuildBattery()
    {
        var b = _ctx.Config.Battery;
        var thresholds = Form.Text(string.Join(", ", b.Thresholds), v =>
        {
            b.Thresholds = v.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
                .Select(x => int.TryParse(x.Trim('%'), out var n) ? n : 0).Where(n => n is > 0 and < 100).Distinct().OrderByDescending(n => n).ToList();
            _ctx.MarkDirty();
        });
        var trayDevices = new List<(string? Value, string Text)> { (null, L("automatic (first device with a battery)", "automatisch (erstes Gerät mit Akku)")) };
        trayDevices.AddRange(_ctx.Host.Devices.Select(d => ((string?)TrayController.ShortName(d.Name), d.Name)).DistinctBy(x => x.Item1));
        if (b.TrayDevice is { } current && trayDevices.All(t => t.Value != current)) trayDevices.Add((current, current));

        return Form.Group(L("Battery notifications", "Akku-Benachrichtigungen"),
            Form.Row(L("Warning thresholds (%)", "Warnschwellen (%)"), thresholds,
                L("Each threshold notifies once per discharge cycle (default: 20, 10, 5).", "Jede Schwelle meldet sich einmal pro Entladezyklus (Standard: 20, 10, 5).")),
            Form.Row("", Form.Check(L("\"Charging complete\" notification", "Meldung \"Aufladen abgeschlossen\""), b.NotifyCharged,
                v => { b.NotifyCharged = v; _ctx.MarkDirty(); })),
            Form.Row(L("Poll interval (min)", "Abfrage-Intervall (min)"), Form.Number(b.PollMinutes, v => { b.PollMinutes = (int)v; _ctx.MarkDirty(); }, 1, 1440),
                L("In addition to the mouse's battery events.", "Zusätzlich zu den Akku-Events der Maus.")),
            Form.Row(L("Device in tray icon", "Gerät im Tray-Symbol"), Form.Choice(trayDevices, b.TrayDevice, v => { b.TrayDevice = v; _ctx.MarkDirty(); })));
    }
}
