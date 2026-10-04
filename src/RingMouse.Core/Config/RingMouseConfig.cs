using System.ComponentModel;
using System.Text.Json.Serialization;

namespace RingMouse.Core.Config;

/// <summary>Wurzel der Konfiguration (%APPDATA%\RingMouse\config.json). Die Datei ist die Quelle der Wahrheit.</summary>
public sealed class RingMouseConfig
{
    [JsonPropertyName("$schema")]
    [JsonPropertyOrder(-10)]
    public string? Schema { get; set; } = "./config.schema.json";

    [Description("Config format version.")]
    public int Version { get; set; } = 1;

    public GeneralSettings General { get; set; } = new();

    [Description("Ring behavior and appearance.")]
    public RingSettings Ring { get; set; } = new();

    [Description("Default button assignment: CID (e.g. \"0x00FD\", see ringmouse-probe controls) → action.")]
    public Dictionary<string, ActionDefinition> Buttons { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [Description("Named rings. A ring has up to 8 segments (null = empty slot). Segment 0 is at the top, then clockwise.")]
    public Dictionary<string, RingDefinition> Rings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [Description("App-specific profiles. The first matching profile wins; anything it doesn't override comes from the default.")]
    public List<ProfileDefinition> Profiles { get; set; } = [];

    [Description("Device settings. Key: \"*\" (all), part of the device name (e.g. \"MX Vertical\") or product ID (\"B020\").")]
    public Dictionary<string, DeviceSettings> Devices { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public BatterySettings Battery { get; set; } = new();

    public DebugSettings Debug { get; set; } = new();
}

public enum AutostartMode
{
    [Description("No autostart")] Off,
    [Description("HKCU\\...\\Run (normal rights)")] Run,
    [Description("Scheduled task with highest privileges (also works in admin windows)")] Task,
}

public enum UiLanguage
{
    [Description("Follow the Windows display language")] Auto,
    [Description("English")] English,
    [Description("German")] German,
}

public sealed class GeneralSettings
{
    [Description("UI language: auto (Windows display language) | english | german")]
    public UiLanguage Language { get; set; } = UiLanguage.Auto;

    [Description("off | run | task")]
    public AutostartMode Autostart { get; set; } = AutostartMode.Off;

    [Description("Warn if Logi Options+ is running (it overrides button diversions).")]
    public bool WarnIfOptionsPlusRunning { get; set; } = true;

    [Description("Turn off the analytics key events enabled by Options+ (every click is transmitted wirelessly).")]
    public bool DisableAnalyticsReporting { get; set; } = true;

    [Description("Serilog level: Verbose | Debug | Information | Warning | Error")]
    public string LogLevel { get; set; } = "Information";

    [Description("Check GitHub for updates and install them automatically (the only network connection RingMouse makes).")]
    public bool AutoUpdate { get; set; } = true;

    [Description("Install a downloaded update once the PC has been idle for this many minutes (1–240).")]
    public int UpdateIdleMinutes { get; set; } = 5;
}

public enum RingMode
{
    [Description("Hold: hold the button, choose a direction, releasing executes")] Hold,
    [Description("Tap: the ring stays open, clicking a segment executes")] Tap,
    [Description("Hybrid: quick tap = Tap, hold + move = Hold")] Hybrid,
}

public enum RawXYUsage
{
    Auto,
    On,
    Off,
}

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

public sealed class RingSettings
{
    [Description("hold | tap | hybrid")]
    public RingMode Mode { get; set; } = RingMode.Hybrid;

    [Description("Outer radius of the ring in DIP (logical pixels).")]
    public double Radius { get; set; } = 150;

    [Description("Deadzone in the center in DIP: releasing/clicking inside it cancels.")]
    public double Deadzone { get; set; } = 26;

    [Description("Hybrid: a tap shorter than this time (ms) keeps the ring open.")]
    public int TapThresholdMs { get; set; } = 350;

    [Description("While holding, pushing outward past a submenu opens it; the button stays pressed and releasing selects there.")]
    public bool SubmenuPush { get; set; } = true;

    [Description("Hide the mouse pointer while holding, as long as a dot shows the raw XY direction (it stays visible in Tap mode).")]
    public bool HideCursor { get; set; } = true;

    [Description("Subtle opening animation.")]
    public bool Animation { get; set; } = true;

    [Description("Move the mouse pointer back to its starting position after closing.")]
    public bool RestoreCursor { get; set; } = true;

    [Description("Raw XY while holding: auto (if the button supports it) | on | off. With raw XY the pointer stays put while holding.")]
    public RawXYUsage UseRawXY { get; set; } = RawXYUsage.Auto;

    [Description("Conversion of raw XY counts → DIP.")]
    public double RawXYScale { get; set; } = 0.6;

    [Description("system | light | dark")]
    public ThemePreference Theme { get; set; } = ThemePreference.System;

    [Description("Base color of the ring \"#RRGGBB\"; empty = matches light/dark. Text and shades are derived from it.")]
    public string? RingColor { get; set; }

    [Description("Highlight color \"#RRGGBB\"; empty = Windows accent color.")]
    public string? AccentColor { get; set; }

    [Description("Ring opacity in percent (30–100).")]
    public int Opacity { get; set; } = 100;

    [Description("Size of icons and labels in percent (50–200), on top of the radius.")]
    public int TextScale { get; set; } = 100;

    [Description("Color of the pointer dot while holding \"#RRGGBB\"; empty = automatic (light on the highlight).")]
    public string? PointerColor { get; set; }

    [Description("Diameter of the pointer dot in DIP (2–40).")]
    public double PointerSize { get; set; } = 9;

    [Description("Show labels below the icons.")]
    public bool ShowLabels { get; set; } = true;

    [Description("Tap mode: close the ring automatically after n seconds (0 = never).")]
    public int AutoCloseSeconds { get; set; } = 8;
}

public sealed class RingDefinition
{
    [Description("Optional title (shown in the center while nothing is selected).")]
    public string? Title { get; set; }

    [Description("2–8 segments, null = empty slot. Index 0 = top, then clockwise.")]
    public List<RingSegment?> Segments { get; set; } = [];
}

public sealed class RingSegment
{
    public string Label { get; set; } = "";

    [Description("Icon name (e.g. \"Lock\"), \"glyph:E72E\", \"file:C:\\\\path\\\\image.png\", \"exe:C:\\\\path\\\\app.exe\" or \"text:AB\".")]
    public string? Icon { get; set; }

    public ActionDefinition? Action { get; set; }
}

public sealed class ProfileDefinition
{
    public string Name { get; set; } = "";

    public bool Enabled { get; set; } = true;

    [Description("Process names of the foreground window, e.g. \"excel.exe\"; wildcards * and ? allowed.")]
    public List<string> Processes { get; set; } = [];

    [Description("Overrides individual buttons (CID → action).")]
    public Dictionary<string, ActionDefinition> Buttons { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [Description("Replaces rings: ring name → other ring name (e.g. \"main\": \"main-excel\").")]
    public Dictionary<string, string> Rings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class DeviceSettings
{
    [Description("Sensor DPI (applied again after every reconnect); null = leave unchanged.")]
    public int? Dpi { get; set; }

    [Description("false = RingMouse leaves this device completely alone.")]
    public bool Enabled { get; set; } = true;
}

public sealed class BatterySettings
{
    [Description("Warning thresholds in percent – each one fires once per discharge cycle.")]
    public List<int> Thresholds { get; set; } = [20, 10, 5];

    [Description("Show a \"charging complete\" notification.")]
    public bool NotifyCharged { get; set; } = true;

    [Description("Polling interval in minutes in case no battery events arrive.")]
    public int PollMinutes { get; set; } = 10;

    [Description("Device for the tray icon (part of the name); null = first device with a battery.")]
    public string? TrayDevice { get; set; }

    [Description("Custom voltage curve for 0x1001: list of {\"mv\":…, \"percent\":…}.")]
    public List<VoltagePoint>? VoltageCurve { get; set; }
}

public sealed class VoltagePoint
{
    public int Mv { get; set; }
    public int Percent { get; set; }
}

public sealed class DebugSettings
{
    [Description("Record all raw HID++ frames in logs\\hidpp-*.log.")]
    public bool RawHidLog { get; set; }

    [Description("Log the ring's opening latency.")]
    public bool LogRingLatency { get; set; } = true;
}
