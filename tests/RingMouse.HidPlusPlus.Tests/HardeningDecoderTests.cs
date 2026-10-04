using RingMouse.HidPlusPlus.Features;
using Xunit;

namespace RingMouse.HidPlusPlus.Tests;

/// <summary>Defense-in-depth-Härtung: Decoder dürfen an zu kurzen (als Short gesendeten) Antworten nicht werfen,
/// DPI-Werte werden begrenzt und die gerätegesteuerte DPI-Range-Expansion ist gedeckelt.</summary>
public class HardeningDecoderTests
{
    public static readonly byte[][] ShortPayloads = [[], [0x01], [0x01, 0x02], [0x01, 0x02, 0x03]];

    [Theory]
    [MemberData(nameof(ShortPayloadCases))]
    public void BatteryDecoders_DoNotThrowOnShortPayload(byte[] payload)
    {
        _ = BatteryDecoder.DecodeUnified(payload);
        _ = BatteryDecoder.DecodeVoltage(payload);
        _ = BatteryDecoder.DecodeStatus(payload); // würde ohne Härtung bei d[2] werfen
    }

    [Theory]
    [MemberData(nameof(ShortPayloadCases))]
    public void ControlDecoders_DoNotThrowOnShortPayload(byte[] payload)
    {
        _ = ControlInfo.Parse(0, payload);        // liest bis d[8]
        _ = ControlReporting.Parse(payload);      // liest bis d[4]
    }

    [Theory]
    [MemberData(nameof(ShortPayloadCases))]
    public void FirmwareDecoders_DoNotThrowOnShortPayload(byte[] payload)
    {
        _ = DeviceFirmwareInfo.Parse(payload);    // liest bis d[14]
        _ = FirmwareEntity.Parse(0, payload);     // liest bis d[10]
    }

    public static IEnumerable<object[]> ShortPayloadCases() => ShortPayloads.Select(p => new object[] { p });

    [Theory]
    [InlineData(-5, 50)]
    [InlineData(0, 50)]
    [InlineData(49, 50)]
    [InlineData(800, 800)]
    [InlineData(32000, 32000)]
    [InlineData(99999, 32000)]
    [InlineData(70000, 32000)] // würde sonst als 16-Bit-Wert (70000 & 0xFFFF) ans Gerät gehen
    public void Clamp_KeepsDpiInPlausibleRange(int input, int expected) =>
        Assert.Equal(expected, DpiFeature.Clamp(input));

    [Fact]
    public void Snap_WithoutSupportedList_ClampsInsteadOfPassingRawValue()
    {
        Assert.Equal(32000, DpiFeature.Snap(99999, []));
        Assert.Equal(50, DpiFeature.Snap(1, []));
        // Mit Liste: nächstgelegener unterstützter Wert.
        Assert.Equal(800, DpiFeature.Snap(900, [400, 800, 1600]));
    }

    [Fact]
    public void ParseValues_RangeExpansion_IsCapped()
    {
        // start=100, Range-Marker step=1, end=0xDFF0 (~57000) → ohne Deckel entstünden zehntausende Werte.
        var values = DpiFeature.ParseValues([0x00, 0x64, 0xE0, 0x01, 0xDF, 0xF0]);
        Assert.True(values.Count <= 2000, $"expected the DPI list to be capped, got {values.Count}");
        Assert.True(values.Count >= 511, "a legitimate large range must still be allowed in full");
        Assert.Equal(100, values[0]);
    }

    [Fact]
    public void ParseValues_NormalList_IsUnchanged()
    {
        var values = DpiFeature.ParseValues([0x03, 0x20, 0x06, 0x40, 0x0C, 0x80, 0x00, 0x00]); // 800, 1600, 3200, Ende
        Assert.Equal([800, 1600, 3200], values);
    }
}
