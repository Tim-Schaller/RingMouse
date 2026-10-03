namespace RingMouse.HidPlusPlus.Receivers;

/// <summary>HID++-1.0-Notification 0x41 (Device Connection) bzw. 0x40 (Device Disconnection) eines Receivers.</summary>
public sealed record ReceiverConnectionEvent(byte DeviceIndex, bool Connected, bool LinkEstablished, byte DeviceType, ushort WirelessPid, byte Protocol)
{
    public override string ToString() =>
        $"Index {DeviceIndex}: {(Connected ? (LinkEstablished ? "verbunden" : "gekoppelt, Funk getrennt") : "entkoppelt")} WPID {WirelessPid:X4} Typ {DeviceType}";
}

/// <summary>Zugriff auf einen Unifying-/Bolt-/Lightspeed-Receiver (HID++ 1.0, Device-Index 0xFF).</summary>
public sealed class HidppReceiver : IDisposable
{
    public const byte RegisterNotifications = 0x00;
    public const byte RegisterConnectionState = 0x02;
    public const byte RegisterPairingInfo = 0xB5;

    public HidppReceiver(HidppChannel channel, byte softwareId = HidppDevice.DefaultSoftwareId)
    {
        Device = new HidppDevice(channel, HidppMessage.DirectDeviceIndex, softwareId);
    }

    /// <summary>Der Receiver selbst (Index 0xFF).</summary>
    public HidppDevice Device { get; }

    /// <summary>Prüft per Register 0x02, ob am Kanal ein Receiver hängt.</summary>
    public async Task<bool> IsReceiverAsync(CancellationToken ct = default)
    {
        try
        {
            await Device.ReadRegisterAsync(RegisterConnectionState, cancellationToken: ct).ConfigureAwait(false);
            return true;
        }
        catch (HidppException)
        {
            return false;
        }
    }

    public async Task<int> GetConnectedDeviceCountAsync(CancellationToken ct = default) =>
        (await Device.ReadRegisterAsync(RegisterConnectionState, cancellationToken: ct).ConfigureAwait(false))[1];

    /// <summary>Aktiviert Wireless-Notifications (Flag 0x000100), ohne andere Flags zu verändern.</summary>
    public async Task EnableWirelessNotificationsAsync(CancellationToken ct = default)
    {
        var current = await Device.ReadRegisterAsync(RegisterNotifications, cancellationToken: ct).ConfigureAwait(false);
        var value = new byte[] { current[0], (byte)(current[1] | 0x01), current[2] };
        await Device.WriteRegisterAsync(RegisterNotifications, value, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>Lässt den Receiver für alle gekoppelten Geräte eine 0x41-Notification senden.</summary>
    public Task TriggerConnectionNotificationsAsync(CancellationToken ct = default) =>
        Device.WriteRegisterAsync(RegisterConnectionState, [0x02, 0x00, 0x00], cancellationToken: ct);

    /// <summary>Gerätename aus dem Pairing-Register (Unifying-Format, bei anderen Receivern ggf. null).</summary>
    public async Task<string?> GetPairedDeviceNameAsync(int deviceIndex, CancellationToken ct = default)
    {
        try
        {
            var r = await Device.ReadRegisterAsync(RegisterPairingInfo, longRegister: true,
                args: [(byte)(0x40 + deviceIndex - 1)], cancellationToken: ct).ConfigureAwait(false);
            var len = Math.Min((int)r[1], r.Length - 2);
            return len <= 0 ? null : System.Text.Encoding.UTF8.GetString(r, 2, len).TrimEnd('\0');
        }
        catch (HidppException)
        {
            return null;
        }
    }

    /// <summary>Erkennt 0x40/0x41-Notifications eines Receivers.</summary>
    public static bool TryParseConnectionEvent(HidppMessage message, out ReceiverConnectionEvent connection)
    {
        connection = null!;
        if (!message.IsShort) return false;
        if (message.DeviceIndex is 0 or > 6) return false;
        var p = message.Payload;
        switch (message.SubId)
        {
            case 0x41:
                connection = new ReceiverConnectionEvent(message.DeviceIndex, true, (p[0] & 0x40) == 0,
                    (byte)(p[0] & 0x0F), (ushort)((p[2] << 8) | p[1]), message.Address);
                return true;
            case 0x40:
                connection = new ReceiverConnectionEvent(message.DeviceIndex, false, false, 0, 0, message.Address);
                return true;
            default:
                return false;
        }
    }

    public void Dispose() => Device.Dispose();
}
