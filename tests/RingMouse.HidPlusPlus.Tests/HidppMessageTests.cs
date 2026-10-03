using Xunit;

namespace RingMouse.HidPlusPlus.Tests;

public class HidppMessageTests
{
    [Fact]
    public void Request20_EncodesFunctionAndSoftwareId()
    {
        var m = HidppMessage.Request20(0xFF, 0x0A, 0x01, 0x0A, [0x05]);

        Assert.Equal(HidppMessage.LongReportId, m.ReportId);
        Assert.Equal(0x1A, m.Address);
        Assert.Equal(1, m.FunctionId);
        Assert.Equal(0x0A, m.SoftwareId);
        Assert.Equal("11 FF 0A 1A 05 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00", m.ToString());
    }

    [Fact]
    public void Request20_RejectsSoftwareIdZero() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => HidppMessage.Request20(0xFF, 0x00, 0x01, 0x00));

    [Fact]
    public void TryParse_ShortAndLong()
    {
        Assert.True(HidppMessage.TryParse(HidppMessage.ParseHex("10 FF 81 02 00 01 00"), out var s));
        Assert.True(s.IsShort);
        Assert.Equal(3, s.Payload.Length);
        Assert.Equal(0x01, s.Payload[1]);

        Assert.True(HidppMessage.TryParse(HidppMessage.ParseHex("11 FF 00 1A 04 05 B3 00 00 00 00 00 00 00 00 00 00 00 00 00"), out var l));
        Assert.True(l.IsLong);
        Assert.Equal(16, l.Payload.Length);
        Assert.Equal(new byte[] { 0x04, 0x05, 0xB3 }, l.Payload[..3].ToArray());
    }

    [Theory]
    [InlineData("20 01 41 00 00 00 00")]   // DJ-Report
    [InlineData("02 00 00")]               // Maus-Report
    [InlineData("11 FF")]                  // zu kurz
    public void TryParse_RejectsNonHidpp(string hex) =>
        Assert.False(HidppMessage.TryParse(HidppMessage.ParseHex(hex), out _));

    [Fact]
    public void ShortLongConversion()
    {
        var shortMsg = HidppMessage.Short(0xFF, 0x81, 0x02, [0x00, 0x00, 0x00]);
        var asLong = shortMsg.ToLong();
        Assert.True(asLong.IsLong);
        Assert.Equal(20, asLong.ToArray().Length);
        Assert.True(asLong.FitsShort);
        Assert.Equal(shortMsg.ToString(), asLong.ToShort().ToString());

        var big = HidppMessage.Long(0xFF, 0x0A, 0x3A, [0, 0xFD, 0x03, 0, 0, 0x02]);
        Assert.False(big.FitsShort);
        Assert.Throws<InvalidOperationException>(() => big.ToShort());
    }

    [Fact]
    public void ErrorDetection()
    {
        var e20 = HidppMessage.Parse("11 FF FF 0A 3A 02 00 00 00 00 00 00 00 00 00 00 00 00 00 00");
        Assert.True(e20.IsHidpp20Error);
        var e10 = HidppMessage.Parse("10 FF 8F 00 1A 01 00");
        Assert.True(e10.IsHidpp10Error);
    }
}

public class ResponseMatcherTests
{
    [Fact]
    public void Hidpp20_MatchesResponseErrorAndIgnoresOthers()
    {
        var matcher = new Hidpp20ResponseMatcher(0xFF, 0x0A, 0x2A);

        Assert.Equal(MatchKind.Response, matcher.Match(HidppMessage.Parse("11 FF 0A 2A 00 FD 01 00 00 00"), out _, out _));

        Assert.Equal(MatchKind.Error, matcher.Match(HidppMessage.Parse("11 FF FF 0A 2A 02 00 00"), out var code, out var v10));
        Assert.Equal(2, code);
        Assert.False(v10);

        Assert.Equal(MatchKind.Error, matcher.Match(HidppMessage.Parse("10 FF 8F 0A 2A 01 00"), out code, out v10));
        Assert.True(v10);

        // Event (SW-ID 0), fremde SW-ID, anderer Feature-Index, anderes Gerät
        Assert.Equal(MatchKind.None, matcher.Match(HidppMessage.Parse("11 FF 0A 20 00 50 01"), out _, out _));
        Assert.Equal(MatchKind.None, matcher.Match(HidppMessage.Parse("11 FF 0A 25 00 FD 01"), out _, out _));
        Assert.Equal(MatchKind.None, matcher.Match(HidppMessage.Parse("11 FF 0B 2A 00 00 00"), out _, out _));
        Assert.Equal(MatchKind.None, matcher.Match(HidppMessage.Parse("11 02 0A 2A 00 00 00"), out _, out _));
    }

    [Fact]
    public void DirectDevice_AcceptsIndexZero()
    {
        var matcher = new Hidpp20ResponseMatcher(0xFF, 0x00, 0x1A);
        Assert.Equal(MatchKind.Response, matcher.Match(HidppMessage.Parse("11 00 00 1A 04 05 00"), out _, out _));
    }

    [Fact]
    public void Hidpp10_MatchesRegisterResponse()
    {
        var matcher = new Hidpp10ResponseMatcher(0xFF, 0x81, 0x02);
        Assert.Equal(MatchKind.Response, matcher.Match(HidppMessage.Parse("10 FF 81 02 00 01 00"), out _, out _));
        Assert.Equal(MatchKind.Error, matcher.Match(HidppMessage.Parse("10 FF 8F 81 02 02 00"), out var code, out _));
        Assert.Equal((byte)Hidpp10Error.InvalidAddress, code);
    }
}
