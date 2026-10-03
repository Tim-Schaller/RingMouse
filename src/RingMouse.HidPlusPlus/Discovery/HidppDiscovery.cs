using RingMouse.HidPlusPlus.Receivers;
using RingMouse.HidPlusPlus.Transport;
using RingMouse.HidPlusPlus.Transport.Windows;

namespace RingMouse.HidPlusPlus.Discovery;

/// <summary>Ein physisches Gerät bzw. Receiver mit seinen HID++-Collections.</summary>
public sealed record HidppEndpoint(
    string Key,
    HidBusType Bus,
    ushort VendorId,
    ushort ProductId,
    string? ProductName,
    HidDeviceInfo? ShortCollection,
    HidDeviceInfo? LongCollection,
    IReadOnlyList<HidDeviceInfo> AllCollections)
{
    public string DisplayName => ProductName ?? $"{VendorId:X4}:{ProductId:X4}";

    public string BusText => Bus switch
    {
        HidBusType.BluetoothLe => "Bluetooth LE",
        HidBusType.BluetoothClassic => "Bluetooth",
        HidBusType.Usb => "USB",
        _ => "unknown",
    };

    /// <summary>
    /// Öffnet die HID++-Collections und startet die Lese-Threads. Mit <paramref name="start"/> = false startet der
    /// Aufrufer selbst (<see cref="HidppChannel.Start"/>), nachdem er seine Handler (z.B. Closed) angemeldet hat.
    /// </summary>
    public HidppChannel Open(IHidTransport transport, bool start = true)
    {
        IHidPort? shortPort = null, longPort = null;
        try
        {
            if (ShortCollection is not null) shortPort = transport.Open(ShortCollection);
            if (LongCollection is not null) longPort = transport.Open(LongCollection);
            var channel = new HidppChannel($"{DisplayName} ({BusText})", shortPort, longPort);
            if (start) channel.Start();
            return channel;
        }
        catch
        {
            shortPort?.Dispose();
            longPort?.Dispose();
            throw;
        }
    }

    public bool ContainsPath(string path) =>
        AllCollections.Any(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));
}

public sealed record ReceiverSlot(byte DeviceIndex, ProtocolVersion? Protocol, string? Name);

/// <summary>Ergebnis der Identifikation eines Endpoints.</summary>
public sealed record EndpointIdentity(bool Responds, bool IsReceiver, ProtocolVersion? DirectProtocol, IReadOnlyList<ReceiverSlot> Slots)
{
    public static EndpointIdentity None { get; } = new(false, false, null, []);
}

public static class HidppDiscovery
{
    public const ushort LogitechVendorId = 0x046D;

    public static bool IsHidppShortCollection(HidDeviceInfo c) =>
        c.UsagePage is 0xFF00 or 0xFF43 && c.InputReportLength == HidppMessage.ShortLength
                                         && c.InputReportIds.Contains(HidppMessage.ShortReportId);

    public static bool IsHidppLongCollection(HidDeviceInfo c) =>
        c.UsagePage is 0xFF00 or 0xFF43 && c.InputReportLength == HidppMessage.LongLength
                                         && c.InputReportIds.Contains(HidppMessage.LongReportId);

    public static bool IsHidppCollection(HidDeviceInfo c) => IsHidppShortCollection(c) || IsHidppLongCollection(c);

    /// <summary>Gruppiert die Collections nach physischem Gerät (gemeinsamer Elternknoten).</summary>
    public static IReadOnlyList<HidppEndpoint> FindEndpoints(IReadOnlyList<HidDeviceInfo> collections)
    {
        var result = new List<HidppEndpoint>();
        foreach (var group in collections.GroupBy(GroupKey, StringComparer.OrdinalIgnoreCase))
        {
            var all = group.OrderBy(c => c.CollectionTag, StringComparer.Ordinal).ToList();
            var shortC = all.FirstOrDefault(IsHidppShortCollection);
            var longC = all.FirstOrDefault(IsHidppLongCollection);
            if (shortC is null && longC is null) continue;

            var first = longC ?? shortC!;
            result.Add(new HidppEndpoint(group.Key, first.Bus, first.VendorId, first.ProductId,
                ResolveProductName(all), shortC, longC, all));
        }
        return result;
    }

    public static IReadOnlyList<HidppEndpoint> FindEndpoints(IHidTransport transport, ushort vendorId = LogitechVendorId) =>
        FindEndpoints(transport.Enumerate(vendorId));

    private static string GroupKey(HidDeviceInfo c)
    {
        if (!string.IsNullOrEmpty(c.ParentInstanceId)) return c.ParentInstanceId;
        var id = c.InstanceId;
        var col = id.IndexOf("&COL", StringComparison.OrdinalIgnoreCase);
        return col > 0 ? id[..col] : id;
    }

    private static string? ResolveProductName(IReadOnlyList<HidDeviceInfo> collections)
    {
        var product = collections.Select(c => c.Product).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
        if (product is not null) return product;

        // Bluetooth LE: Name steht am Großeltern-Knoten (BTHLE\Dev_...).
        var parent = collections.Select(c => c.ParentInstanceId).FirstOrDefault(p => !string.IsNullOrEmpty(p));
        if (parent is null) return null;
        var grand = WinHidTransport.GetParentInstanceId(parent);
        return (grand is null ? null : WinHidTransport.GetFriendlyName(grand)) ?? WinHidTransport.GetFriendlyName(parent);
    }

    /// <summary>
    /// Stellt fest, ob am Kanal ein direkt verbundenes HID++-Gerät (Index 0xFF) oder ein Receiver hängt,
    /// und listet bei Receivern die antwortenden Geräte 1–6.
    /// </summary>
    public static async Task<EndpointIdentity> IdentifyAsync(HidppChannel channel, HidBusType bus,
        byte softwareId = HidppDevice.DefaultSoftwareId, CancellationToken ct = default)
    {
        using var direct = new HidppDevice(channel, HidppMessage.DirectDeviceIndex, softwareId)
        {
            Timeout = TimeSpan.FromMilliseconds(1500),
        };

        ProtocolVersion? protocol = null;
        try
        {
            protocol = await direct.GetProtocolVersionAsync(ct).ConfigureAwait(false);
        }
        catch (HidppException)
        {
            // weiter unten als Receiver versuchen
        }

        if (protocol is { IsHidpp20: true } || bus is HidBusType.BluetoothLe or HidBusType.BluetoothClassic)
            return new EndpointIdentity(protocol is not null, false, protocol, []);

        using var receiver = new HidppReceiver(channel, softwareId);
        receiver.Device.Timeout = TimeSpan.FromMilliseconds(1000);
        if (!await receiver.IsReceiverAsync(ct).ConfigureAwait(false))
            return new EndpointIdentity(protocol is not null, false, protocol, []);

        var slots = new List<ReceiverSlot>();
        for (byte index = 1; index <= 6; index++)
        {
            using var dev = new HidppDevice(channel, index, softwareId) { Timeout = TimeSpan.FromMilliseconds(700) };
            ProtocolVersion? pv = null;
            try
            {
                pv = await dev.GetProtocolVersionAsync(ct).ConfigureAwait(false);
            }
            catch (HidppErrorException)
            {
                // nicht gekoppelt
            }
            catch (HidppTimeoutException)
            {
                // gekoppelt, aber schläft evtl. – Name trotzdem versuchen
            }

            var name = await receiver.GetPairedDeviceNameAsync(index, ct).ConfigureAwait(false);
            if (pv is not null || name is not null) slots.Add(new ReceiverSlot(index, pv, name));
        }
        return new EndpointIdentity(true, true, null, slots);
    }
}
