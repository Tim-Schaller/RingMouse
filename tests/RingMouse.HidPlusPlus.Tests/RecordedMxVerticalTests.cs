using RingMouse.HidPlusPlus.Features;
using Xunit;

namespace RingMouse.HidPlusPlus.Tests;

/// <summary>
/// Tests mit den echten Rohdaten der MX Vertical (Bluetooth LE, PID B020), aufgezeichnet mit
/// <c>ringmouse-probe dump --record</c>. Die Aufzeichnung wird Frame für Frame abgespielt.
/// </summary>
public class RecordedMxVerticalTests
{
    private const string DumpFixture = "mx-vertical-dump.jsonl";

    private static IReadOnlyList<RecordedFrame> Frames => Recording.Load(DumpFixture);

    private static (HidppChannel Channel, HidppDevice Device, FakeHidPort Port) Replay()
    {
        var responder = new ReplayResponder(Frames);
        var port = new FakeHidPort(responder.Respond);
        var channel = new HidppChannel("replay", null, port);
        var device = new HidppDevice(channel, HidppMessage.DirectDeviceIndex) { Timeout = TimeSpan.FromSeconds(2) };
        return (channel, device, port);
    }

    [Fact]
    public void Ping_Response_IsProtocol45()
    {
        var r = Recording.ResponseTo(Frames, "11 FF 00 1A");
        Assert.Equal(4, r.Payload[0]);
        Assert.Equal(5, r.Payload[1]);
    }

    [Fact]
    public void DpiResponses_ParseRealFrames()
    {
        var list = AdjustableDpiFeature.ParseDpiList(Recording.ResponseTo(Frames, "11 FF 12 1A 00").Payload);
        Assert.Equal(400, list[0]);
        Assert.Equal(4000, list[^1]);
        Assert.Equal(37, list.Count);
    }

    [Fact]
    public void Firmware_ParseRealFrames()
    {
        var info = DeviceFirmwareInfo.Parse(Recording.ResponseTo(Frames, "11 FF 02 0A").Payload);
        Assert.Equal(3, info.EntityCount);
        Assert.Equal("1A2B3C4D", info.UnitIdText); // in der Aufzeichnung anonymisiert
        Assert.Contains((ushort)0xB020, info.ModelIds);

        var main = FirmwareEntity.Parse(1, Recording.ResponseTo(Frames, "11 FF 02 1A 01").Payload);
        Assert.Equal(0, main.Type);
        Assert.Equal("MPM 16.00.B0009", main.VersionText);
        Assert.True(main.Active);
    }

    [Fact]
    public void AnalyticsEvents_InRecording_AreRecognised()
    {
        var analytics = Recording.ReceivedMessages(Frames)
            .Where(m => ReprogControlsV4Feature.TryParseAnalytics(m, 0x0A, out _))
            .ToList();
        Assert.NotEmpty(analytics);
    }

    [Fact]
    public async Task Replay_FeatureTable()
    {
        var (channel, device, _) = Replay();
        using (channel)
        using (device)
        {
            Assert.Equal(new ProtocolVersion(4, 5), await device.GetProtocolVersionAsync());

            var features = await device.EnumerateFeaturesAsync();
            Assert.Equal(30, features.Count);
            Assert.Equal(FeatureIds.Root, features[0].FeatureId);
            Assert.Equal(FeatureIds.ReprogControlsV4, features[10].FeatureId);
            Assert.Equal(4, features[10].Version);
            Assert.Equal(FeatureIds.BatteryStatus, features[8].FeatureId);
            Assert.Equal(FeatureIds.WirelessDeviceStatus, features[4].FeatureId);
            Assert.Equal(FeatureIds.AdjustableDpi, features[18].FeatureId);
            Assert.True(features.Single(f => f.FeatureId == 0x18B1).IsHidden);
        }
    }

    [Fact]
    public async Task Replay_Controls_Name_Battery_Dpi()
    {
        var (channel, device, _) = Replay();
        using (channel)
        using (device)
        {
            // wie im aufgezeichneten Dump: Feature-Tabelle zuerst (füllt den Index-Cache)
            await device.EnumerateFeaturesAsync();
            Assert.Equal("MX Vertical Advanced Ergonomic Mouse", await DeviceIdentity.GetNameAsync(device));
            Assert.Equal(DeviceKind.Mouse, await DeviceIdentity.GetKindAsync(device));

            var rc = await ReprogControlsV4Feature.TryCreateAsync(device);
            Assert.NotNull(rc);
            var controls = await rc!.GetAllControlsAsync();
            Assert.Equal(7, controls.Count);
            var dpiSwitch = controls.Single(c => c.ControlId == 0x00FD);
            Assert.True(dpiSwitch.IsDivertable);
            Assert.True(dpiSwitch.SupportsRawXY);
            Assert.Equal(0x00D2, dpiSwitch.TaskId);
            Assert.False(controls.Single(c => c.ControlId == 0x0050).IsDivertable);
            Assert.True(controls.Single(c => c.ControlId == 0x00D7).IsVirtual);

            var battery = await BatteryFeature.DetectAsync(device);
            Assert.NotNull(battery);
            Assert.Equal(BatterySource.BatteryStatus, battery!.Source);
            var reading = await battery.ReadAsync();
            Assert.Equal(20, reading.Percent);
            Assert.Equal(ChargeState.Discharging, reading.State);

            var dpi = await AdjustableDpiFeature.TryCreateAsync(device);
            Assert.NotNull(dpi);
            Assert.Equal(1000, (await dpi!.GetDpiAsync()).CurrentDpi);
        }
    }
}
