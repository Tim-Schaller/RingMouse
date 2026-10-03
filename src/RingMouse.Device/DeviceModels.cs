using RingMouse.Core.Config;
using RingMouse.HidPlusPlus;
using RingMouse.HidPlusPlus.Features;

namespace RingMouse.Device;

public enum DeviceState
{
    /// <summary>Wird gerade (neu) konfiguriert.</summary>
    Connecting,
    /// <summary>Konfiguriert, Umleitungen aktiv.</summary>
    Ready,
    /// <summary>Gerät antwortet nicht (schläft, außer Reichweite, getrennt). Letzter Akkustand bleibt gültig.</summary>
    Unreachable,
}

/// <summary>Unveränderlicher Zustand eines Geräts für UI, Tray und Logs.</summary>
public sealed record DeviceSnapshot
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public ushort ProductId { get; init; }
    public string? UnitId { get; init; }
    public required string Connection { get; init; }
    public DeviceState State { get; init; }
    public DeviceKind Kind { get; init; } = DeviceKind.Unknown;
    public ProtocolVersion? Protocol { get; init; }
    public BatteryReading? Battery { get; init; }
    public DateTimeOffset? BatteryTimestamp { get; init; }
    public int? Dpi { get; init; }
    public IReadOnlyList<int> SupportedDpi { get; init; } = [];
    public IReadOnlyList<ControlInfo> Controls { get; init; } = [];
    public IReadOnlySet<ushort> DivertedControls { get; init; } = new HashSet<ushort>();
    /// <summary>Gewünschte Umleitungen, die das Gerät nicht kann/ablehnt (→ Hook-Fallback für Standardtasten).</summary>
    public IReadOnlySet<ushort> FailedControls { get; init; } = new HashSet<ushort>();
    public bool HasBattery => Battery is not null;

    public string StateText => State switch
    {
        DeviceState.Ready => "verbunden",
        DeviceState.Connecting => "verbindet …",
        _ => "nicht erreichbar",
    };
}

public readonly record struct ButtonEvent(string DeviceKey, ushort ControlId, bool IsDown, long Timestamp);

public readonly record struct RawXYEvent(string DeviceKey, short Dx, short Dy, long Timestamp);

/// <summary>Was der DeviceService auf den Geräten einstellen soll (aus der Config abgeleitet).</summary>
public sealed record DeviceConfiguration
{
    public IReadOnlySet<ushort> DivertControls { get; init; } = new HashSet<ushort>();
    public IReadOnlySet<ushort> RawXYControls { get; init; } = new HashSet<ushort>();
    public bool DisableAnalytics { get; init; } = true;
    /// <summary>Umleitungen anderer Software (z.B. Options+) auf nicht belegten Tasten aufheben.</summary>
    public bool ClearForeignDiversions { get; init; } = true;
    public TimeSpan BatteryPollInterval { get; init; } = TimeSpan.FromMinutes(10);
    public BatteryVoltageCurve? VoltageCurve { get; init; }
    public Func<string?, ushort, string?, DeviceSettings> SettingsFor { get; init; } = (_, _, _) => new DeviceSettings();

    /// <summary>Nichts verändern (Startzustand, bevor die Config geladen ist).</summary>
    public static DeviceConfiguration Empty { get; } = new() { DisableAnalytics = false, ClearForeignDiversions = false };
}

public sealed class DeviceServiceOptions
{
    public byte SoftwareId { get; init; } = HidppDevice.DefaultSoftwareId;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromMilliseconds(1500);
    public TimeSpan WatchdogInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan RescanInterval { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Steht die Hauptschleife so lange still, bricht der Wächter den Durchlauf ab bzw. startet sie neu.</summary>
    public TimeSpan LoopStallTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>Höchstdauer einer HID-Enumeration, bevor der Scan übersprungen wird.</summary>
    public TimeSpan EnumerationTimeout { get; init; } = TimeSpan.FromSeconds(15);
}
