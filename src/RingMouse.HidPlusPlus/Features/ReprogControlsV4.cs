namespace RingMouse.HidPlusPlus.Features;

/// <summary>Fähigkeiten einer Taste laut REPROG_CONTROLS_V4.getCidInfo (Byte 4 | Byte 8 &lt;&lt; 8).</summary>
[Flags]
public enum ControlFlags : ushort
{
    None = 0,
    MouseButton = 0x0001,
    FnKey = 0x0002,
    HotKey = 0x0004,
    FnSensitive = 0x0008,
    Reprogrammable = 0x0010,
    Divertable = 0x0020,
    PersistentlyDivertable = 0x0040,
    Virtual = 0x0080,
    RawXY = 0x0100,
    ForceRawXY = 0x0200,
    AnalyticsKeyEvents = 0x0400,
    RawWheel = 0x0800,
}

/// <summary>Flags-Byte von setCidReporting (jeweils Wert + "valid"-Bit).</summary>
[Flags]
public enum ReportingFlags : byte
{
    None = 0,
    Divert = 0x01,
    DivertValid = 0x02,
    Persist = 0x04,
    PersistValid = 0x08,
    RawXY = 0x10,
    RawXYValid = 0x20,
    ForceRawXY = 0x40,
    ForceRawXYValid = 0x80,
}

public sealed record ControlInfo(int Index, ushort ControlId, ushort TaskId, ControlFlags Flags, byte Position, byte Group, byte GroupMask)
{
    public string Name => ControlIds.GetName(ControlId);
    public bool IsDivertable => Flags.HasFlag(ControlFlags.Divertable);
    public bool IsPersistentlyDivertable => Flags.HasFlag(ControlFlags.PersistentlyDivertable);
    public bool IsVirtual => Flags.HasFlag(ControlFlags.Virtual);
    public bool SupportsRawXY => Flags.HasFlag(ControlFlags.RawXY);
    public bool IsMouseButton => Flags.HasFlag(ControlFlags.MouseButton);

    public string FlagsText
    {
        get
        {
            var f = new List<string>(8);
            if (Flags.HasFlag(ControlFlags.MouseButton)) f.Add("mse");
            if (Flags.HasFlag(ControlFlags.FnKey)) f.Add("fn");
            if (Flags.HasFlag(ControlFlags.HotKey)) f.Add("hotkey");
            if (Flags.HasFlag(ControlFlags.FnSensitive)) f.Add("fn-sens");
            if (Flags.HasFlag(ControlFlags.Reprogrammable)) f.Add("reprog");
            if (Flags.HasFlag(ControlFlags.Divertable)) f.Add("divertable");
            if (Flags.HasFlag(ControlFlags.PersistentlyDivertable)) f.Add("persist");
            if (Flags.HasFlag(ControlFlags.Virtual)) f.Add("virtual");
            if (Flags.HasFlag(ControlFlags.RawXY)) f.Add("rawXY");
            if (Flags.HasFlag(ControlFlags.ForceRawXY)) f.Add("forceRawXY");
            if (Flags.HasFlag(ControlFlags.AnalyticsKeyEvents)) f.Add("analytics");
            if (Flags.HasFlag(ControlFlags.RawWheel)) f.Add("rawWheel");
            return string.Join(",", f);
        }
    }

    /// <summary>getCidInfo: [CID(2) TID(2) Flags Pos Group GMask AdditionalFlags].</summary>
    public static ControlInfo Parse(int index, ReadOnlySpan<byte> d)
    {
        // Fehlende Bytes (kurze/fehlerhafte Antwort) als 0 behandeln statt eine IndexOutOfRangeException zu werfen.
        static byte B(ReadOnlySpan<byte> s, int i) => i < s.Length ? s[i] : (byte)0;
        var flags = (ControlFlags)(B(d, 4) | (B(d, 8) << 8));
        return new ControlInfo(index, (ushort)((B(d, 0) << 8) | B(d, 1)), (ushort)((B(d, 2) << 8) | B(d, 3)), flags, B(d, 5), B(d, 6), B(d, 7));
    }
}

/// <summary>Aktueller Reporting-Zustand einer Taste (getCidReporting).</summary>
public sealed record ControlReporting(ushort ControlId, bool Diverted, bool PersistentlyDiverted, bool RawXY, bool ForceRawXY,
    ushort RemappedTo, bool AnalyticsKeyEvents)
{
    public bool IsRemapped => RemappedTo != 0 && RemappedTo != ControlId;
    public bool IsDefault => !Diverted && !PersistentlyDiverted && !RawXY && !ForceRawXY && !IsRemapped;

    public string StateText
    {
        get
        {
            var s = new List<string>(5);
            if (Diverted) s.Add("divert");
            if (PersistentlyDiverted) s.Add("PERSIST");
            if (RawXY) s.Add("rawXY");
            if (ForceRawXY) s.Add("forceRawXY");
            if (IsRemapped) s.Add($"remap→0x{RemappedTo:X4}");
            if (AnalyticsKeyEvents) s.Add("analytics");
            return s.Count == 0 ? "native" : string.Join(",", s);
        }
    }

    /// <summary>[CID(2) Flags Remap(2) Flags2] – Valid-Bits werden ignoriert.</summary>
    public static ControlReporting Parse(ReadOnlySpan<byte> d)
    {
        static byte B(ReadOnlySpan<byte> s, int i) => i < s.Length ? s[i] : (byte)0; // kurze Antwort nicht crashen lassen
        var flags = B(d, 2);
        return new ControlReporting(
            (ushort)((B(d, 0) << 8) | B(d, 1)),
            (flags & 0x01) != 0,
            (flags & 0x04) != 0,
            (flags & 0x10) != 0,
            (flags & 0x40) != 0,
            (ushort)((B(d, 3) << 8) | B(d, 4)),
            d.Length > 5 && (d[5] & 0x01) != 0);
    }
}

/// <summary>REPROG_CONTROLS_V4 (0x1B04): Tasten auflisten, Diversion setzen/prüfen, Events dekodieren.</summary>
public sealed class ReprogControlsV4Feature
{
    public const int EventDivertedButtons = 0;
    public const int EventRawXY = 1;
    public const int EventAnalytics = 2;

    private readonly HidppDevice _device;

    private ReprogControlsV4Feature(HidppDevice device, FeatureInfo feature)
    {
        _device = device;
        Feature = feature;
    }

    public FeatureInfo Feature { get; }
    public byte FeatureIndex => Feature.Index;

    public static async Task<ReprogControlsV4Feature?> TryCreateAsync(HidppDevice device, CancellationToken ct = default)
    {
        var feature = await device.GetFeatureAsync(FeatureIds.ReprogControlsV4, ct).ConfigureAwait(false);
        return feature is null ? null : new ReprogControlsV4Feature(device, feature);
    }

    public async Task<int> GetCountAsync(CancellationToken ct = default) =>
        (await _device.CallAsync(FeatureIndex, 0x00, null, ct).ConfigureAwait(false))[0];

    public async Task<ControlInfo> GetControlInfoAsync(int index, CancellationToken ct = default) =>
        ControlInfo.Parse(index, await _device.CallAsync(FeatureIndex, 0x01, [(byte)index], ct).ConfigureAwait(false));

    public async Task<IReadOnlyList<ControlInfo>> GetAllControlsAsync(CancellationToken ct = default)
    {
        var count = await GetCountAsync(ct).ConfigureAwait(false);
        var list = new List<ControlInfo>(count);
        for (var i = 0; i < count; i++) list.Add(await GetControlInfoAsync(i, ct).ConfigureAwait(false));
        return list;
    }

    public async Task<ControlReporting> GetReportingAsync(ushort controlId, CancellationToken ct = default) =>
        ControlReporting.Parse(await _device.CallAsync(FeatureIndex, 0x02, [(byte)(controlId >> 8), (byte)controlId], ct)
            .ConfigureAwait(false));

    /// <summary>setCidReporting. <paramref name="remapTo"/> = 0 lässt das Remapping unverändert.</summary>
    public async Task<ControlReporting> SetReportingAsync(ushort controlId, ReportingFlags flags, ushort remapTo = 0,
        CancellationToken ct = default)
    {
        var r = await _device.CallAsync(FeatureIndex, 0x03,
            [(byte)(controlId >> 8), (byte)controlId, (byte)flags, (byte)(remapTo >> 8), (byte)remapTo], ct).ConfigureAwait(false);
        return ControlReporting.Parse(r);
    }

    /// <summary>Temporäre Diversion setzen/aufheben; optional Raw-XY mitschalten.</summary>
    public Task<ControlReporting> SetDivertAsync(ushort controlId, bool divert, bool? rawXY = null, CancellationToken ct = default)
    {
        var flags = ReportingFlags.DivertValid | (divert ? ReportingFlags.Divert : 0);
        if (rawXY is { } raw) flags |= ReportingFlags.RawXYValid | (raw ? ReportingFlags.RawXY : 0);
        return SetReportingAsync(controlId, flags, 0, ct);
    }

    /// <summary>Stellt einen früher gelesenen Zustand (Divert/Persist/RawXY) wieder her.</summary>
    public Task<ControlReporting> RestoreAsync(ControlReporting previous, CancellationToken ct = default)
    {
        var flags = ReportingFlags.DivertValid | ReportingFlags.PersistValid | ReportingFlags.RawXYValid;
        if (previous.Diverted) flags |= ReportingFlags.Divert;
        if (previous.PersistentlyDiverted) flags |= ReportingFlags.Persist;
        if (previous.RawXY) flags |= ReportingFlags.RawXY;
        return SetReportingAsync(previous.ControlId, flags, 0, ct);
    }

    /// <summary>Hebt eine dauerhafte Diversion (z.B. von Options+ hinterlassen) auf.</summary>
    public Task<ControlReporting> ClearAllDiversionAsync(ushort controlId, CancellationToken ct = default) =>
        SetReportingAsync(controlId, ReportingFlags.DivertValid | ReportingFlags.PersistValid | ReportingFlags.RawXYValid, 0, ct);

    /// <summary>
    /// Schaltet die Analytics-Key-Events (Event 2, jede Tastenbetätigung) ein/aus. Options+ aktiviert sie;
    /// ohne Host-Software erzeugen sie nur Funkverkehr. Byte 5: Bit0 = Wert, Bit1 = gültig.
    /// </summary>
    public async Task<ControlReporting> SetAnalyticsReportingAsync(ushort controlId, bool enabled, CancellationToken ct = default)
    {
        var r = await _device.CallAsync(FeatureIndex, 0x03,
            [(byte)(controlId >> 8), (byte)controlId, 0x00, 0x00, 0x00, (byte)(enabled ? 0x03 : 0x02)], ct).ConfigureAwait(false);
        return ControlReporting.Parse(r);
    }

    /// <summary>Event 2: Analytics-Key-Events [(CID(2) Ereignis)…], nur zur Anzeige.</summary>
    public static bool TryParseAnalytics(HidppMessage message, byte featureIndex, out (ushort ControlId, byte Event)[] events)
    {
        events = [];
        if (!HidppEvents.IsEvent(message, featureIndex, EventAnalytics)) return false;
        var p = message.Payload;
        var list = new List<(ushort, byte)>();
        for (var i = 0; i + 2 < p.Length; i += 3)
        {
            var cid = (ushort)((p[i] << 8) | p[i + 1]);
            if (cid == 0) break;
            list.Add((cid, p[i + 2]));
        }
        events = [.. list];
        return true;
    }

    /// <summary>Event 0: bis zu vier aktuell gedrückte, divertete CIDs (leer = alle losgelassen).</summary>
    public static bool TryParseDivertedButtons(HidppMessage message, byte featureIndex, out ushort[] pressed)
    {
        pressed = [];
        if (!HidppEvents.IsEvent(message, featureIndex, EventDivertedButtons)) return false;
        var p = message.Payload;
        var list = new List<ushort>(4);
        for (var i = 0; i + 1 < p.Length && i < 8; i += 2)
        {
            var cid = (ushort)((p[i] << 8) | p[i + 1]);
            if (cid != 0) list.Add(cid);
        }
        pressed = [.. list];
        return true;
    }

    /// <summary>Event 1: Raw-XY-Bewegung (dx, dy vorzeichenbehaftet, Big Endian).</summary>
    public static bool TryParseRawXY(HidppMessage message, byte featureIndex, out short dx, out short dy)
    {
        dx = dy = 0;
        if (!HidppEvents.IsEvent(message, featureIndex, EventRawXY)) return false;
        var p = message.Payload;
        if (p.Length < 4) return false; // als Short gesendetes/verkürztes Event ignorieren statt zu werfen
        dx = (short)((p[0] << 8) | p[1]);
        dy = (short)((p[2] << 8) | p[3]);
        return true;
    }
}
