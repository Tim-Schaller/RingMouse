using RingMouse.HidPlusPlus.Features;
using RingMouse.HidPlusPlus.Receivers;
using Xunit;

namespace RingMouse.HidPlusPlus.Tests;

public class FeatureDecoderTests
{
    [Fact]
    public void Battery_Status1000()
    {
        var r = BatteryDecoder.DecodeStatus([0x14, 0x0A, 0x00]);
        Assert.Equal(20, r.Percent);
        Assert.Equal(ChargeState.Discharging, r.State);
        Assert.Equal(BatteryLevel.Low, r.Level);
        Assert.False(r.ExternalPower);

        var full = BatteryDecoder.DecodeStatus([0x00, 0x00, 0x03]);
        Assert.Equal(ChargeState.Full, full.State);
        Assert.Equal(100, full.Percent);

        Assert.Equal(ChargeState.Charging, BatteryDecoder.DecodeStatus([0x50, 0x5A, 0x01]).State);
        Assert.Equal(ChargeState.Error, BatteryDecoder.DecodeStatus([0x50, 0x5A, 0x06]).State);
    }

    [Fact]
    public void Battery_Unified1004()
    {
        var r = BatteryDecoder.DecodeUnified([0x55, 0x04, 0x01, 0x01]);
        Assert.Equal(85, r.Percent);
        Assert.Equal(BatteryLevel.Good, r.Level);
        Assert.Equal(ChargeState.Charging, r.State);
        Assert.True(r.ExternalPower);

        var noSoc = BatteryDecoder.DecodeUnified([0x00, 0x02, 0x00, 0x00], stateOfChargeSupported: false);
        Assert.Null(noSoc.Percent);
        Assert.Equal(20, noSoc.EffectivePercent);
        Assert.Equal(ChargeState.Full, BatteryDecoder.DecodeUnified([0x64, 0x08, 0x03, 0x01]).State);
    }

    [Fact]
    public void Battery_Voltage1001()
    {
        var r = BatteryDecoder.DecodeVoltage([0x0F, 0x3C, 0x00]); // 3900 mV
        Assert.Equal(3900, r.VoltageMillivolts);
        Assert.Equal(68, r.Percent);
        Assert.Equal(ChargeState.Discharging, r.State);
        Assert.True(r.PercentIsEstimated);

        Assert.Equal(ChargeState.Charging, BatteryDecoder.DecodeVoltage([0x0F, 0x3C, 0x80]).State);
        var full = BatteryDecoder.DecodeVoltage([0x10, 0x68, 0x81]);
        Assert.Equal(ChargeState.Full, full.State);
        Assert.Equal(100, full.Percent);
        Assert.Equal(ChargeState.ChargingSlow, BatteryDecoder.DecodeVoltage([0x0F, 0x3C, 0x90]).State);
    }

    [Theory]
    [InlineData(4300, 100)]
    [InlineData(4180, 100)]
    [InlineData(3915, 70)]
    [InlineData(3720, 20)]
    [InlineData(3300, 0)]
    public void VoltageCurve_Interpolates(int millivolts, int expected) =>
        Assert.Equal(expected, BatteryVoltageCurve.Default.ToPercent(millivolts));

    [Fact]
    public void Dpi_ListWithRange()
    {
        var list = AdjustableDpiFeature.ParseDpiList(HidppMessage.ParseHex("00 01 90 E0 64 0F A0 00 00"));
        Assert.Equal(37, list.Count);
        Assert.Equal(400, list[0]);
        Assert.Equal(500, list[1]);
        Assert.Equal(4000, list[^1]);
        Assert.Equal(1600, DpiFeature.Snap(1620, list));
    }

    [Fact]
    public void Dpi_DiscreteList() =>
        Assert.Equal(new[] { 400, 800, 1600 }, AdjustableDpiFeature.ParseDpiList(HidppMessage.ParseHex("00 01 90 03 20 06 40 00 00")));

    [Fact]
    public void ExtendedDpi_ListContinuesAcrossPages_EvenInsideAValue()
    {
        // je Seite [Sensor, Richtung, Seite, 13 Byte Werte] – 3200 (0C 80) ist auf zwei Seiten verteilt
        byte[] page0 = HidppMessage.ParseHex("00 00 00 01 90 03 20 04 B0 06 40 07 D0 09 60 0C");
        byte[] page1 = HidppMessage.ParseHex("00 00 01 80 00 00 00 00 00 00 00 00 00 00 00 00");
        Assert.Equal(new[] { 400, 800, 1200, 1600, 2000, 2400, 3200 }, ExtendedAdjustableDpiFeature.ParseDpiRangePages([page0, page1]));
    }

    [Fact]
    public void ExtendedDpi_RangeWithStep()
    {
        var list = ExtendedAdjustableDpiFeature.ParseDpiRangePages([HidppMessage.ParseHex("00 00 00 00 64 E0 32 64 00 00 00 00 00 00 00 00")]);
        Assert.Equal(511, list.Count); // 100 … 25600 in 50er-Schritten
        Assert.Equal(100, list[0]);
        Assert.Equal(150, list[1]);
        Assert.Equal(25600, list[^1]);
    }

    [Fact]
    public void ExtendedDpi_State()
    {
        var s = ExtendedAdjustableDpiFeature.ParseState(HidppMessage.ParseHex("00 06 40 03 E8 06 40 03 E8 02 00 00 00 00 00 00"));
        Assert.Equal(new ExtendedDpiState(0, 1600, 1000, 1600, 1000, 2), s);
    }

    [Fact]
    public void Reprog_DivertedButtonsAndRawXY()
    {
        Assert.True(ReprogControlsV4Feature.TryParseDivertedButtons(HidppMessage.Parse("11 FF 0A 00 00 FD 00 53 00 00"), 0x0A, out var pressed));
        Assert.Equal(new ushort[] { 0x00FD, 0x0053 }, pressed);

        Assert.True(ReprogControlsV4Feature.TryParseDivertedButtons(HidppMessage.Parse("11 FF 0A 00 00 00 00 00"), 0x0A, out pressed));
        Assert.Empty(pressed);

        Assert.True(ReprogControlsV4Feature.TryParseRawXY(HidppMessage.Parse("11 FF 0A 10 FF FE 00 03"), 0x0A, out var dx, out var dy));
        Assert.Equal(-2, dx);
        Assert.Equal(3, dy);

        // Antwort mit SW-ID ist kein Event, anderer Index auch nicht
        Assert.False(ReprogControlsV4Feature.TryParseDivertedButtons(HidppMessage.Parse("11 FF 0A 0A 00 FD 00"), 0x0A, out _));
        Assert.False(ReprogControlsV4Feature.TryParseDivertedButtons(HidppMessage.Parse("11 FF 0B 00 00 FD 00"), 0x0A, out _));
    }

    [Fact]
    public void Reprog_ControlInfoAndReporting()
    {
        var info = ControlInfo.Parse(5, HidppMessage.ParseHex("00 FD 00 D2 71 00 02 03 05"));
        Assert.Equal(0x00FD, info.ControlId);
        Assert.Equal(0x00D2, info.TaskId);
        Assert.True(info.IsDivertable);
        Assert.True(info.IsPersistentlyDivertable);
        Assert.True(info.SupportsRawXY);
        Assert.True(info.Flags.HasFlag(ControlFlags.AnalyticsKeyEvents));
        Assert.Equal("DPI Switch", info.Name);

        var rep = ControlReporting.Parse(HidppMessage.ParseHex("00 FD 11 00 00 01"));
        Assert.True(rep.Diverted);
        Assert.True(rep.RawXY);
        Assert.False(rep.PersistentlyDiverted);
        Assert.True(rep.AnalyticsKeyEvents);
        Assert.False(rep.IsRemapped);
        Assert.Equal("divert,rawXY,analytics", rep.StateText);

        Assert.True(ControlReporting.Parse(HidppMessage.ParseHex("00 53 00 00 56 00")).IsRemapped);
    }

    [Fact]
    public void Reprog_Analytics()
    {
        Assert.True(ReprogControlsV4Feature.TryParseAnalytics(HidppMessage.Parse("11 FF 0A 20 00 50 01 00 51 00"), 0x0A, out var events));
        Assert.Equal(2, events.Length);
        Assert.Equal((0x0050, (byte)1), (events[0].ControlId, events[0].Event));
        Assert.Equal((0x0051, (byte)0), (events[1].ControlId, events[1].Event));
    }

    [Fact]
    public void WirelessStatus_Reconnect()
    {
        Assert.True(WirelessStatusEvent.TryParse(HidppMessage.Parse("11 FF 04 00 01 01 01"), 0x04, out var ws));
        Assert.True(ws.IsReconnection);
        Assert.True(ws.ReconfigurationNeeded);
        Assert.True(ws.PowerSwitchActivated);
    }

    [Fact]
    public void Receiver_ConnectionNotification()
    {
        Assert.True(HidppReceiver.TryParseConnectionEvent(HidppMessage.Parse("10 02 41 04 02 8A 40"), out var c));
        Assert.Equal(2, c.DeviceIndex);
        Assert.True(c.Connected);
        Assert.True(c.LinkEstablished);
        Assert.Equal(0x408A, c.WirelessPid);

        Assert.True(HidppReceiver.TryParseConnectionEvent(HidppMessage.Parse("10 02 41 04 42 8A 40"), out c));
        Assert.False(c.LinkEstablished);

        Assert.False(HidppReceiver.TryParseConnectionEvent(HidppMessage.Parse("11 02 41 04 42 8A 40"), out _));
    }

    [Theory]
    [InlineData("0x00FD", 0x00FD)]
    [InlineData("00fd", 0x00FD)]
    [InlineData("fd", 0x00FD)]
    [InlineData("253", 253)]
    [InlineData("c253", 253)]
    public void ControlIds_Parse(string text, int expected)
    {
        Assert.True(ControlIds.TryParse(text, out var cid));
        Assert.Equal(expected, cid);
    }

    [Fact]
    public void ControlIds_StandardButtons()
    {
        Assert.Equal(StandardMouseButton.Back, ControlIds.GetStandardMouseButton(0x0053));
        Assert.Equal(StandardMouseButton.Forward, ControlIds.GetStandardMouseButton(0x0056));
        Assert.Equal(StandardMouseButton.Middle, ControlIds.GetStandardMouseButton(0x0052));
        Assert.Equal(StandardMouseButton.None, ControlIds.GetStandardMouseButton(0x00FD));
    }

    [Fact]
    public void FeatureInfo_Flags() =>
        Assert.Equal("hidden,engineering", new FeatureInfo(0x18B1, 15, 0x60, 0).FlagsText);
}
