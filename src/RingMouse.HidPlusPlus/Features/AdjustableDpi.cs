namespace RingMouse.HidPlusPlus.Features;

public sealed record DpiState(int SensorIndex, int CurrentDpi, int DefaultDpi);

/// <summary>ADJUSTABLE_DPI (0x2201).</summary>
public sealed class AdjustableDpiFeature
{
    private readonly HidppDevice _device;

    private AdjustableDpiFeature(HidppDevice device, FeatureInfo feature)
    {
        _device = device;
        Feature = feature;
    }

    public FeatureInfo Feature { get; }
    public byte FeatureIndex => Feature.Index;

    public static async Task<AdjustableDpiFeature?> TryCreateAsync(HidppDevice device, CancellationToken ct = default)
    {
        var feature = await device.GetFeatureAsync(FeatureIds.AdjustableDpi, ct).ConfigureAwait(false);
        return feature is null ? null : new AdjustableDpiFeature(device, feature);
    }

    public async Task<int> GetSensorCountAsync(CancellationToken ct = default) =>
        (await _device.CallAsync(FeatureIndex, 0x00, null, ct).ConfigureAwait(false))[0];

    public async Task<IReadOnlyList<int>> GetDpiListAsync(int sensor = 0, CancellationToken ct = default) =>
        ParseDpiList(await _device.CallAsync(FeatureIndex, 0x01, [(byte)sensor], ct).ConfigureAwait(false));

    public async Task<DpiState> GetDpiAsync(int sensor = 0, CancellationToken ct = default)
    {
        var r = await _device.CallAsync(FeatureIndex, 0x02, [(byte)sensor], ct).ConfigureAwait(false);
        var current = (r[1] << 8) | r[2];
        var def = (r[3] << 8) | r[4];
        return new DpiState(r[0], current, def == 0 ? current : def);
    }

    public async Task<int> SetDpiAsync(int dpi, int sensor = 0, CancellationToken ct = default)
    {
        var r = await _device.CallAsync(FeatureIndex, 0x03, [(byte)sensor, (byte)(dpi >> 8), (byte)dpi], ct).ConfigureAwait(false);
        return (r[1] << 8) | r[2];
    }

    /// <summary>
    /// getSensorDpiList: [Sensor, Werte (BE, 2 Byte) ..., 0]. Ein Wert ≥ 0xE000 ist eine Schrittweite
    /// zwischen dem vorigen und dem nächsten Wert (Bereich), sonst ist es ein einzelner DPI-Wert.
    /// </summary>
    public static IReadOnlyList<int> ParseDpiList(ReadOnlySpan<byte> d)
    {
        var raw = new List<int>();
        for (var i = 1; i + 1 < d.Length; i += 2)
        {
            var v = (d[i] << 8) | d[i + 1];
            if (v == 0) break;
            raw.Add(v);
        }

        var result = new List<int>();
        for (var i = 0; i < raw.Count; i++)
        {
            var v = raw[i];
            if (v >= 0xE000)
            {
                var step = v & 0x1FFF;
                if (step == 0 || result.Count == 0 || i + 1 >= raw.Count) continue;
                var start = result[^1];
                var end = raw[i + 1];
                for (var dpi = start + step; dpi < end; dpi += step) result.Add(dpi);
                continue;
            }
            if (result.Count == 0 || result[^1] != v) result.Add(v);
        }
        return result;
    }

    /// <summary>Nächstgelegener unterstützter Wert.</summary>
    public static int Snap(int dpi, IReadOnlyList<int> supported) =>
        supported.Count == 0 ? dpi : supported.MinBy(v => Math.Abs(v - dpi));
}
