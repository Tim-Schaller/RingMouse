using System.Globalization;

namespace RingMouse.Core.Config;

/// <summary>Ordnet ein Gerät den Einträgen unter "devices" zu (spezifischer Eintrag vor "*").</summary>
public static class DeviceMatching
{
    public static DeviceSettings Resolve(RingMouseConfig config, string? deviceName, ushort productId, string? unitId)
    {
        DeviceSettings? specific = null;
        foreach (var (key, settings) in config.Devices)
        {
            if (key == "*") continue;
            if (Matches(key, deviceName, productId, unitId))
            {
                specific = settings;
                break;
            }
        }

        var fallback = config.Devices.GetValueOrDefault("*");
        if (specific is null) return fallback ?? new DeviceSettings();
        if (fallback is null) return specific;
        return new DeviceSettings
        {
            Dpi = specific.Dpi ?? fallback.Dpi,
            Enabled = specific.Enabled && fallback.Enabled,
        };
    }

    public static bool Matches(string key, string? deviceName, ushort productId, string? unitId)
    {
        var k = key.Trim();
        if (k.Length == 0) return false;
        if (!string.IsNullOrEmpty(deviceName) && deviceName.Contains(k, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrEmpty(unitId) && string.Equals(unitId, k, StringComparison.OrdinalIgnoreCase)) return true;

        var hex = k.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? k[2..] : k;
        return hex.Length == 4 && ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var pid) && pid == productId;
    }

    /// <summary>true, wenn der Gerätename zum Tray-Filter passt (null = jedes Gerät).</summary>
    public static bool MatchesTrayFilter(string? filter, string? deviceName) =>
        string.IsNullOrWhiteSpace(filter) || (deviceName?.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase) ?? false);
}
