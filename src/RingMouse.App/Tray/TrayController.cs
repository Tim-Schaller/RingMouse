using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using RingMouse.Device;
using RingMouse.Platform.Display;

namespace RingMouse.App.Tray;

public enum TrayNotice
{
    Info,
    Warning,
    Error,
}

/// <summary>Tray-Symbol mit Akku-Anzeige, Tooltip, Kontextmenü und Benachrichtigungen (erscheinen unter Windows 11 als Toast).</summary>
internal sealed partial class TrayController : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly MenuItem _statusItem;
    private readonly MenuItem _rawLogItem;
    private readonly MenuItem _warningItem;
    private DeviceSnapshot? _device;
    private System.Drawing.Icon? _currentIcon;
    private string? _lastKnown;
    private string? _warning;

    public TrayController(Action openSettings, Action openConfig, Action openLogs, Action<bool> toggleRawLog, Action reconnect, Action exit)
    {
        _statusItem = new MenuItem { Header = "Kein Gerät", IsEnabled = false };
        _warningItem = new MenuItem { Header = "", Visibility = Visibility.Collapsed, IsEnabled = false };
        _rawLogItem = new MenuItem { Header = "Raw-HID++-Log aufzeichnen", IsCheckable = true };
        _rawLogItem.Click += (_, _) => toggleRawLog(_rawLogItem.IsChecked);

        var settings = new MenuItem { Header = "Einstellungen …", FontWeight = FontWeights.SemiBold };
        settings.Click += (_, _) => openSettings();
        var config = new MenuItem { Header = "Config-Datei öffnen" };
        config.Click += (_, _) => openConfig();
        var logs = new MenuItem { Header = "Logs öffnen" };
        logs.Click += (_, _) => openLogs();
        var reconnectItem = new MenuItem { Header = "Geräte neu verbinden" };
        reconnectItem.Click += (_, _) => reconnect();
        var exitItem = new MenuItem { Header = "Beenden" };
        exitItem.Click += (_, _) => exit();

        var menu = new ContextMenu();
        menu.Items.Add(_statusItem);
        menu.Items.Add(_warningItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(settings);
        menu.Items.Add(config);
        menu.Items.Add(logs);
        menu.Items.Add(_rawLogItem);
        menu.Items.Add(reconnectItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);

        _icon = new TaskbarIcon
        {
            ToolTipText = "RingMouse",
            ContextMenu = menu,
            MenuActivation = PopupActivationMode.RightClick,
            NoLeftClickDelay = true,
        };
        _icon.TrayLeftMouseUp += (_, _) => openSettings();
        SetIcon(BatteryIcon.Render(null, false, false, SystemTheme.SystemUsesLightTheme, IconSize()));
        // Effizienzmodus aus: er würde den Prozess drosseln und den Ring verzögern.
        _icon.ForceCreate(enablesEfficiencyMode: false);
    }

    public bool RawLogChecked
    {
        set => _rawLogItem.IsChecked = value;
    }

    /// <summary>Keine Benachrichtigungen anzeigen (Selbsttest, --quiet).</summary>
    public bool Quiet { get; set; }

    /// <summary>Gerät fürs Tray-Icon (null = keins). <paramref name="lastKnown"/>: gespeicherter Stand, falls es schläft.</summary>
    public void UpdateDevice(DeviceSnapshot? device, int? lastPercent, DateTimeOffset? lastTime)
    {
        _device = device;
        _lastKnown = lastPercent is { } p && lastTime is { } t ? $"zuletzt {p} % ({t:dd.MM. HH:mm})" : null;

        var battery = device?.Battery;
        var connected = device?.State == DeviceState.Ready;
        var percent = battery?.EffectivePercent ?? (device is null || !connected ? lastPercent : null);
        var powered = battery is { } b && (b.IsCharging || b.ExternalPower || b.IsFull);
        SetIcon(BatteryIcon.Render(percent, powered && connected, connected, SystemTheme.SystemUsesLightTheme, IconSize()));
        _statusItem.Header = StatusLine(device) ?? "Kein Gerät gefunden";
        UpdateTooltip();
    }

    public void SetWarning(string? warning)
    {
        _warning = warning;
        _warningItem.Header = warning ?? "";
        _warningItem.Visibility = warning is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateTooltip();
    }

    public void RefreshTheme() => UpdateDevice(_device, null, null);

    public void Notify(string title, string message, TrayNotice kind = TrayNotice.Info)
    {
        if (Quiet) return;
        try
        {
            _icon.ShowNotification(title, message, kind switch
            {
                TrayNotice.Warning => NotificationIcon.Warning,
                TrayNotice.Error => NotificationIcon.Error,
                _ => NotificationIcon.Info,
            });
        }
        catch
        {
            // Benachrichtigungen evtl. per Richtlinie aus
        }
    }

    private void UpdateTooltip()
    {
        var sb = new StringBuilder("RingMouse");
        if (StatusLine(_device) is { } line) sb.Append('\n').Append(line);
        else if (_lastKnown is not null) sb.Append('\n').Append(_lastKnown);
        if (_warning is not null) sb.Append('\n').Append(_warning);
        var text = sb.ToString();
        _icon.ToolTipText = text.Length > 127 ? text[..127] : text;
    }

    private string? StatusLine(DeviceSnapshot? d)
    {
        if (d is null) return null;
        var name = ShortName(d.Name);
        if (d.State != DeviceState.Ready)
            return $"{name}: nicht erreichbar{(_lastKnown is null ? "" : " · " + _lastKnown)}";
        var battery = d.Battery is not { } b ? "kein Akku-Wert"
            : b.EffectivePercent is { } p ? $"{p} % · {ChargeText(b.State)}"
            // z.B. beim Laden der MX Vertical: das Gerät meldet dann keinen Prozentwert
            : $"{ChargeText(b.State)} · Stand unbekannt{(_lastKnown is null ? "" : ", " + _lastKnown)}";
        return $"{name}: {battery} · {d.Connection}";
    }

    internal static string ShortName(string name) =>
        name.Replace(" Advanced Ergonomic Mouse", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" Wireless Mouse", "", StringComparison.OrdinalIgnoreCase);

    internal static string ChargeText(RingMouse.HidPlusPlus.Features.ChargeState state) => state switch
    {
        RingMouse.HidPlusPlus.Features.ChargeState.Charging => "lädt",
        RingMouse.HidPlusPlus.Features.ChargeState.ChargingSlow => "lädt langsam",
        RingMouse.HidPlusPlus.Features.ChargeState.Full => "voll",
        RingMouse.HidPlusPlus.Features.ChargeState.NotCharging => "am Kabel",
        RingMouse.HidPlusPlus.Features.ChargeState.Error => "Ladefehler",
        RingMouse.HidPlusPlus.Features.ChargeState.Discharging => "entlädt",
        _ => "?",
    };

    /// <summary>WPF-Bitmap → System.Drawing.Icon (PNG im ICO-Container, verlustfrei) und altes Icon freigeben.</summary>
    private void SetIcon(System.Windows.Media.Imaging.BitmapSource bitmap)
    {
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var png = new MemoryStream();
        encoder.Save(png);
        var data = png.ToArray();

        using var ico = new MemoryStream();
        using (var w = new BinaryWriter(ico, Encoding.UTF8, leaveOpen: true))
        {
            var size = bitmap.PixelWidth;
            w.Write((ushort)0);
            w.Write((ushort)1);
            w.Write((ushort)1);
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : bitmap.PixelHeight));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((ushort)1);
            w.Write((ushort)32);
            w.Write(data.Length);
            w.Write(22);
            w.Write(data);
        }
        ico.Position = 0;
        var icon = new System.Drawing.Icon(ico, bitmap.PixelWidth, bitmap.PixelHeight);
        _icon.Icon = icon;
        _currentIcon?.Dispose();
        _currentIcon = icon;
    }

    private static int IconSize()
    {
        const int SM_CXSMICON = 49;
        var dpi = GetDpiForSystem();
        var size = GetSystemMetricsForDpi(SM_CXSMICON, dpi == 0 ? 96 : dpi);
        return size > 0 ? size : 16;
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForSystem();

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int nIndex, uint dpi);

    public void Dispose()
    {
        _icon.Dispose();
        _currentIcon?.Dispose();
    }
}
