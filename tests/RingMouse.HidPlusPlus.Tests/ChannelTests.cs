using Xunit;

namespace RingMouse.HidPlusPlus.Tests;

public class ChannelTests
{
    [Fact]
    public async Task Timeout_WhenDeviceDoesNotAnswer()
    {
        var port = new FakeHidPort(_ => []);
        using var channel = new HidppChannel("t", null, port);
        using var device = new HidppDevice(channel, 0xFF) { Timeout = TimeSpan.FromMilliseconds(80) };

        await Assert.ThrowsAsync<HidppTimeoutException>(() => device.CallAsync(0x0A, 0x02, [0x00, 0xFD]));
    }

    [Fact]
    public async Task ErrorResponse_BecomesException()
    {
        var port = new FakeHidPort(req => [HidppMessage.ParseHex($"11 FF FF {req[2]:X2} {req[3]:X2} 02 00 00 00 00 00 00 00 00 00 00 00 00 00 00")]);
        using var channel = new HidppChannel("t", null, port);
        using var device = new HidppDevice(channel, 0xFF);

        var ex = await Assert.ThrowsAsync<HidppErrorException>(() => device.CallAsync(0x0A, 0x03, [0x00, 0xFD, 0x03]));
        Assert.Equal(Hidpp20Error.InvalidArgument, ex.Error20);
    }

    [Fact]
    public async Task ForeignSoftwareId_IsIgnored_OwnIsMatched()
    {
        var port = new FakeHidPort(req =>
        [
            HidppMessage.ParseHex($"11 FF {req[2]:X2} {(req[3] & 0xF0) | 0x05:X2} 99 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00"),
            HidppMessage.ParseHex($"11 FF {req[2]:X2} {req[3]:X2} 42 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00"),
        ]);
        using var channel = new HidppChannel("t", null, port);
        using var device = new HidppDevice(channel, 0xFF);
        var foreign = new List<HidppMessage>();
        device.Notification += (_, m, _) => foreign.Add(m);

        var result = await device.CallAsync(0x05, 0x01);
        Assert.Equal(0x42, result[0]);
        await Task.Delay(20);
        Assert.Single(foreign);
        Assert.Equal(0x05, foreign[0].SoftwareId);
    }

    [Fact]
    public async Task Events_AreRoutedAsNotifications()
    {
        var port = new FakeHidPort(_ => []);
        using var channel = new HidppChannel("t", null, port);
        using var device = new HidppDevice(channel, 0xFF);
        var received = new TaskCompletionSource<HidppMessage>();
        device.Notification += (_, m, _) => received.TrySetResult(m);

        port.Inject(HidppMessage.ParseHex("11 FF 0A 00 00 FD 00 00 00 00 00 00 00 00 00 00 00 00 00 00"));

        var m = await received.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(0x0A, m.FeatureIndex);
        Assert.Equal(0, m.SoftwareId);
    }

    [Fact]
    public async Task PortClosed_FailsPendingRequests()
    {
        var port = new FakeHidPort(_ => []);
        using var channel = new HidppChannel("t", null, port);
        using var device = new HidppDevice(channel, 0xFF) { Timeout = TimeSpan.FromSeconds(5) };
        var closed = new TaskCompletionSource();
        channel.Closed += (_, _) => closed.TrySetResult();

        var call = device.CallAsync(0x00, 0x01, [0, 0, 1]);
        port.Fail(new IOException("Gerät weg"));

        await Assert.ThrowsAsync<HidppTransportException>(() => call);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(channel.IsClosed);
        Assert.Throws<HidppTransportException>(() => channel.Send(HidppMessage.Long(0xFF, 0, 0x1A)));
    }

    [Fact]
    public void ShortMessage_IsSentAsLong_WhenOnlyLongCollectionExists()
    {
        var port = new FakeHidPort(_ => []);
        using var channel = new HidppChannel("t", null, port);
        channel.Send(HidppMessage.Short(0xFF, 0x81, 0x02));

        var written = Assert.Single(port.Written);
        Assert.Equal(20, written.Length);
        Assert.Equal(HidppMessage.LongReportId, written[0]);
        Assert.Equal(0x81, written[2]);
    }

    [Fact]
    public async Task Requests_AreSerialisedPerDevice()
    {
        var concurrent = 0;
        var maxConcurrent = 0;
        var port = new FakeHidPort(req =>
        {
            var now = Interlocked.Increment(ref concurrent);
            maxConcurrent = Math.Max(maxConcurrent, now);
            Interlocked.Decrement(ref concurrent);
            return [req];
        });
        using var channel = new HidppChannel("t", null, port);
        using var device = new HidppDevice(channel, 0xFF);

        await Task.WhenAll(Enumerable.Range(0, 10).Select(i => device.CallAsync(0x05, 0x01, [(byte)i])));
        Assert.Equal(10, port.Written.Count);
        Assert.Equal(1, maxConcurrent);
    }
}
