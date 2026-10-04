namespace RingMouse.HidPlusPlus.Features;

/// <summary>Zustand laut getSensorDpi (0x2202): getrennte X/Y-Werte und LOD (Lift-off-Distanz).</summary>
public sealed record ExtendedDpiState(int SensorIndex, int DpiX, int DefaultDpiX, int DpiY, int DefaultDpiY, byte Lod);

/// <summary>
/// EXTENDED_ADJUSTABLE_DPI (0x2202) neuerer Mäuse. Wie 0x2201, aber mit getrennten X/Y-Werten, LOD und einer
/// seitenweise gelieferten DPI-Liste. RingMouse setzt X und Y immer gleich und lässt den LOD-Wert unverändert.
/// Funktionen: 0 getSensorCount, 1 getSensorCapabilities, 2 getSensorDpiRanges, 5 getSensorDpi, 6 setSensorDpi.
/// </summary>
public sealed class ExtendedAdjustableDpiFeature : IDpiFeature
{
    private const int MaxListPages = 16;
    private readonly HidppDevice _device;

    private ExtendedAdjustableDpiFeature(HidppDevice device, FeatureInfo feature)
    {
        _device = device;
        Feature = feature;
    }

    public FeatureInfo Feature { get; }
    public byte FeatureIndex => Feature.Index;
    public ushort FeatureId => FeatureIds.ExtendedAdjustableDpi;

    public static async Task<ExtendedAdjustableDpiFeature?> TryCreateAsync(HidppDevice device, CancellationToken ct = default)
    {
        var feature = await device.GetFeatureAsync(FeatureIds.ExtendedAdjustableDpi, ct).ConfigureAwait(false);
        return feature is null ? null : new ExtendedAdjustableDpiFeature(device, feature);
    }

    public async Task<int> GetSensorCountAsync(CancellationToken ct = default) =>
        (await _device.CallAsync(FeatureIndex, 0x00, null, ct).ConfigureAwait(false))[0];

    /// <summary>DPI-Liste der X-Richtung: Seiten abfragen, bis die Liste mit 0 endet.</summary>
    public async Task<IReadOnlyList<int>> GetDpiListAsync(int sensor = 0, CancellationToken ct = default)
    {
        var pages = new List<byte[]>();
        for (var page = 0; page < MaxListPages; page++)
        {
            var r = await _device.CallAsync(FeatureIndex, 0x02, [(byte)sensor, 0, (byte)page], ct).ConfigureAwait(false);
            pages.Add(r);
            if (ListComplete(pages)) break;
        }
        return ParseDpiRangePages(pages);
    }

    public async Task<DpiState> GetDpiAsync(int sensor = 0, CancellationToken ct = default)
    {
        var s = await GetExtendedDpiAsync(sensor, ct).ConfigureAwait(false);
        return new DpiState(s.SensorIndex, s.DpiX, s.DefaultDpiX == 0 ? s.DpiX : s.DefaultDpiX);
    }

    public async Task<ExtendedDpiState> GetExtendedDpiAsync(int sensor = 0, CancellationToken ct = default) =>
        ParseState(await _device.CallAsync(FeatureIndex, 0x05, [(byte)sensor], ct).ConfigureAwait(false));

    public async Task<int> SetDpiAsync(int dpi, int sensor = 0, CancellationToken ct = default)
    {
        dpi = DpiFeature.Clamp(dpi);
        var lod = (await GetExtendedDpiAsync(sensor, ct).ConfigureAwait(false)).Lod;
        var r = await _device.CallAsync(FeatureIndex, 0x06,
            [(byte)sensor, (byte)(dpi >> 8), (byte)dpi, (byte)(dpi >> 8), (byte)dpi, lod], ct).ConfigureAwait(false);
        var echoed = r.Length >= 3 ? (r[1] << 8) | r[2] : 0;
        return echoed == 0 ? dpi : echoed;
    }

    /// <summary>getSensorDpi: [Sensor, X (2), Standard-X (2), Y (2), Standard-Y (2), LOD].</summary>
    public static ExtendedDpiState ParseState(ReadOnlySpan<byte> r)
    {
        var data = r.ToArray(); // fehlende Bytes (kurze Antwort) zählen als 0
        int Word(int i) => data.Length > i + 1 ? (data[i] << 8) | data[i + 1] : 0;
        return new ExtendedDpiState(data.Length > 0 ? data[0] : 0, Word(1), Word(3), Word(5), Word(7), data.Length > 9 ? data[9] : (byte)0);
    }

    /// <summary>
    /// getSensorDpiRanges liefert je Seite [Sensor, Richtung, Seite, Werte …]. Die Werte laufen über Seitengrenzen
    /// hinweg weiter (auch mitten in einem 2-Byte-Wert) und werden deshalb erst zusammengesetzt, dann dekodiert.
    /// </summary>
    public static IReadOnlyList<int> ParseDpiRangePages(IEnumerable<byte[]> pages) => DpiFeature.ParseValues(Concat(pages));

    private static bool ListComplete(IEnumerable<byte[]> pages)
    {
        var bytes = Concat(pages);
        for (var i = 0; i + 1 < bytes.Length; i += 2)
            if (bytes[i] == 0 && bytes[i + 1] == 0) return true;
        return false;
    }

    private static byte[] Concat(IEnumerable<byte[]> pages) => pages.SelectMany(p => p.Length > 3 ? p.Skip(3) : []).ToArray();
}
