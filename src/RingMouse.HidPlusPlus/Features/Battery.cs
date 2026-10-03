namespace RingMouse.HidPlusPlus.Features;

public enum ChargeState
{
    Unknown,
    Discharging,
    Charging,
    ChargingSlow,
    Full,
    NotCharging,
    Error,
}

public enum BatteryLevel
{
    Unknown,
    Critical,
    Low,
    Good,
    Full,
}

public enum BatterySource
{
    None,
    UnifiedBattery,  // 0x1004
    BatteryVoltage,  // 0x1001
    BatteryStatus,   // 0x1000
}

/// <summary>Ein normalisierter Akku-Messwert, egal aus welchem Feature.</summary>
public sealed record BatteryReading(
    int? Percent,
    BatteryLevel Level,
    ChargeState State,
    bool ExternalPower,
    int? VoltageMillivolts,
    BatterySource Source,
    bool PercentIsEstimated)
{
    public bool IsCharging => State is ChargeState.Charging or ChargeState.ChargingSlow;
    public bool IsFull => State == ChargeState.Full;

    /// <summary>Prozent, notfalls grob aus der Stufe geschätzt.</summary>
    public int? EffectivePercent => Percent ?? Level switch
    {
        BatteryLevel.Critical => 5,
        BatteryLevel.Low => 20,
        BatteryLevel.Good => 60,
        BatteryLevel.Full => 100,
        _ => null,
    };

    public static BatteryLevel LevelFromPercent(int? percent) => percent switch
    {
        null => BatteryLevel.Unknown,
        <= 5 => BatteryLevel.Critical,
        <= 20 => BatteryLevel.Low,
        < 90 => BatteryLevel.Good,
        _ => BatteryLevel.Full,
    };
}

/// <summary>
/// Spannungs→Prozent-Kurve für 0x1001 (Li-Ion/Li-Po unter Mauslast, eigene Stützstellen,
/// lineare Interpolation). Über die Config ersetzbar.
/// </summary>
public sealed class BatteryVoltageCurve
{
    private readonly (int Millivolts, int Percent)[] _points;

    public BatteryVoltageCurve(IEnumerable<(int Millivolts, int Percent)> points)
    {
        _points = points.OrderByDescending(p => p.Millivolts).ToArray();
        if (_points.Length < 2) throw new ArgumentException("At least two curve points are required.", nameof(points));
    }

    public static BatteryVoltageCurve Default { get; } = new(
    [
        (4180, 100), (4100, 93), (4020, 85), (3950, 76), (3900, 68), (3860, 60), (3830, 52), (3800, 44),
        (3775, 36), (3750, 28), (3720, 20), (3690, 13), (3650, 8), (3600, 5), (3550, 3), (3500, 1), (3400, 0),
    ]);

    public IReadOnlyList<(int Millivolts, int Percent)> Points => _points;

    public int ToPercent(int millivolts)
    {
        if (millivolts >= _points[0].Millivolts) return _points[0].Percent;
        if (millivolts <= _points[^1].Millivolts) return _points[^1].Percent;
        for (var i = 0; i < _points.Length - 1; i++)
        {
            var (hiMv, hiPct) = _points[i];
            var (loMv, loPct) = _points[i + 1];
            if (millivolts > hiMv || millivolts < loMv) continue;
            var t = (double)(millivolts - loMv) / (hiMv - loMv);
            return (int)Math.Round(loPct + t * (hiPct - loPct));
        }
        return _points[^1].Percent;
    }
}

/// <summary>Dekodiert die Antworten/Events der drei Akku-Features (jeweils gleiches Format für Read und Event 0).</summary>
public static class BatteryDecoder
{
    /// <summary>0x1004 getStatus / Event 0: [SoC %, Level-Bits, Ladestatus, externe Versorgung].</summary>
    public static BatteryReading DecodeUnified(ReadOnlySpan<byte> d, bool stateOfChargeSupported = true)
    {
        var levelBits = d[1];
        var level = (levelBits & 0x08) != 0 ? BatteryLevel.Full
            : (levelBits & 0x04) != 0 ? BatteryLevel.Good
            : (levelBits & 0x02) != 0 ? BatteryLevel.Low
            : (levelBits & 0x01) != 0 ? BatteryLevel.Critical
            : BatteryLevel.Unknown;
        var state = d[2] switch
        {
            0 => ChargeState.Discharging,
            1 => ChargeState.Charging,
            2 => ChargeState.ChargingSlow,
            3 => ChargeState.Full,
            4 => ChargeState.Error,
            _ => ChargeState.Unknown,
        };
        int? percent = stateOfChargeSupported ? Math.Clamp((int)d[0], 0, 100) : null;
        return new BatteryReading(percent, level, state, d[3] != 0, null, BatterySource.UnifiedBattery, false);
    }

    /// <summary>0x1001 getBatteryInfo / Event 0: [Spannung mV (BE, 2 Byte), Flags].</summary>
    public static BatteryReading DecodeVoltage(ReadOnlySpan<byte> d, BatteryVoltageCurve? curve = null)
    {
        var millivolts = (d[0] << 8) | d[1];
        var flags = d[2];
        var external = (flags & 0x80) != 0;
        var state = !external
            ? ChargeState.Discharging
            : (flags & 0x07) switch
            {
                0 => (flags & 0x10) != 0 ? ChargeState.ChargingSlow : ChargeState.Charging,
                1 => ChargeState.Full,
                2 => ChargeState.NotCharging,
                7 => ChargeState.Error,
                _ => ChargeState.Charging,
            };

        var percent = (curve ?? BatteryVoltageCurve.Default).ToPercent(millivolts);
        if (state == ChargeState.Full) percent = 100;
        var level = (flags & 0x20) != 0 ? BatteryLevel.Critical : BatteryReading.LevelFromPercent(percent);
        return new BatteryReading(percent, level, state, external, millivolts, BatterySource.BatteryVoltage, true);
    }

    /// <summary>0x1000 getBatteryLevelStatus / Event 0: [Entladestufe %, nächste Stufe %, Status].</summary>
    public static BatteryReading DecodeStatus(ReadOnlySpan<byte> d)
    {
        var state = d[2] switch
        {
            0 => ChargeState.Discharging,
            1 => ChargeState.Charging,
            2 => ChargeState.Charging,      // "almost full"
            3 => ChargeState.Full,
            4 => ChargeState.ChargingSlow,
            5 or 6 or 7 => ChargeState.Error,
            _ => ChargeState.Unknown,
        };
        int? percent = d[0] == 0 ? null : Math.Clamp((int)d[0], 0, 100);
        if (state == ChargeState.Full) percent ??= 100;
        var external = state is ChargeState.Charging or ChargeState.ChargingSlow or ChargeState.Full;
        return new BatteryReading(percent, BatteryReading.LevelFromPercent(percent), state, external, null, BatterySource.BatteryStatus, false);
    }
}

/// <summary>Akku-Zugriff mit automatischer Feature-Wahl: 0x1004 → 0x1001 → 0x1000.</summary>
public sealed class BatteryFeature
{
    private readonly HidppDevice _device;
    private readonly bool _stateOfChargeSupported;

    private BatteryFeature(HidppDevice device, FeatureInfo feature, BatterySource source, bool socSupported, bool rechargeable)
    {
        _device = device;
        Feature = feature;
        Source = source;
        _stateOfChargeSupported = socSupported;
        Rechargeable = rechargeable;
    }

    public FeatureInfo Feature { get; }
    public BatterySource Source { get; }
    public bool Rechargeable { get; }
    public byte FeatureIndex => Feature.Index;
    public BatteryVoltageCurve Curve { get; set; } = BatteryVoltageCurve.Default;

    public static async Task<BatteryFeature?> DetectAsync(HidppDevice device, CancellationToken ct = default)
    {
        var unified = await device.GetFeatureAsync(FeatureIds.UnifiedBattery, ct).ConfigureAwait(false);
        if (unified is not null)
        {
            var caps = await device.CallAsync(unified.Index, 0x00, null, ct).ConfigureAwait(false);
            // caps[1]: Bit0 = wiederaufladbar, Bit1 = Ladezustand in % verfügbar
            return new BatteryFeature(device, unified, BatterySource.UnifiedBattery, (caps[1] & 0x02) != 0, (caps[1] & 0x01) != 0);
        }

        var voltage = await device.GetFeatureAsync(FeatureIds.BatteryVoltage, ct).ConfigureAwait(false);
        if (voltage is not null) return new BatteryFeature(device, voltage, BatterySource.BatteryVoltage, true, true);

        var status = await device.GetFeatureAsync(FeatureIds.BatteryStatus, ct).ConfigureAwait(false);
        return status is null ? null : new BatteryFeature(device, status, BatterySource.BatteryStatus, true, true);
    }

    public async Task<BatteryReading> ReadAsync(CancellationToken ct = default)
    {
        var function = Source == BatterySource.UnifiedBattery ? (byte)0x01 : (byte)0x00;
        var data = await _device.CallAsync(FeatureIndex, function, null, ct).ConfigureAwait(false);
        return Decode(data);
    }

    public bool TryDecodeEvent(HidppMessage message, out BatteryReading reading)
    {
        reading = null!;
        if (!HidppEvents.IsEvent(message, FeatureIndex, 0)) return false;
        reading = Decode(message.Payload);
        return true;
    }

    private BatteryReading Decode(ReadOnlySpan<byte> data) => Source switch
    {
        BatterySource.UnifiedBattery => BatteryDecoder.DecodeUnified(data, _stateOfChargeSupported),
        BatterySource.BatteryVoltage => BatteryDecoder.DecodeVoltage(data, Curve),
        _ => BatteryDecoder.DecodeStatus(data),
    };
}
