using System.Collections.Concurrent;
using RingMouse.HidPlusPlus.Features;

namespace RingMouse.HidPlusPlus;

public readonly record struct ProtocolVersion(int Major, int Minor)
{
    public bool IsHidpp20 => Major >= 2;
    public override string ToString() => $"{Major}.{Minor}";
}

/// <summary>Ein Eintrag der Feature-Tabelle eines Geräts.</summary>
public sealed record FeatureInfo(ushort FeatureId, byte Index, byte Type, byte Version)
{
    public string Name => FeatureIds.GetName(FeatureId);

    public bool IsObsolete => (Type & 0x80) != 0;
    public bool IsHidden => (Type & 0x40) != 0;
    public bool IsEngineering => (Type & 0x20) != 0;
    public bool IsManufacturingDeactivatable => (Type & 0x10) != 0;
    public bool IsComplianceDeactivatable => (Type & 0x08) != 0;

    public string FlagsText
    {
        get
        {
            var flags = new List<string>(5);
            if (IsObsolete) flags.Add("obsolete");
            if (IsHidden) flags.Add("hidden");
            if (IsEngineering) flags.Add("engineering");
            if (IsManufacturingDeactivatable) flags.Add("mfg-deact");
            if (IsComplianceDeactivatable) flags.Add("compl-deact");
            return string.Join(",", flags);
        }
    }
}

/// <summary>
/// Logisches HID++-Gerät auf einem Kanal (Device-Index 0xFF = direkt, 1–6 = am Receiver).
/// Serialisiert Anfragen, cacht die Feature-Tabelle und filtert Notifications nach Device-Index.
/// </summary>
public sealed class HidppDevice : IDisposable
{
    /// <summary>Eigene Software-ID (unteres Nibble des Function-Bytes), um eigene Antworten zu erkennen.</summary>
    public const byte DefaultSoftwareId = 0x0A;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<ushort, FeatureInfo?> _features = new();
    private int _disposed;

    public HidppDevice(HidppChannel channel, byte deviceIndex, byte softwareId = DefaultSoftwareId)
    {
        if (softwareId is 0 or > 0x0F) throw new ArgumentOutOfRangeException(nameof(softwareId), "Software-ID muss 1–15 sein.");
        Channel = channel;
        DeviceIndex = deviceIndex;
        SoftwareId = softwareId;
        channel.MessageReceived += OnChannelMessage;
    }

    public HidppChannel Channel { get; }
    public byte DeviceIndex { get; }
    public byte SoftwareId { get; }
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Alle unzugeordneten Nachrichten dieses Geräts (Events mit SW-ID 0, fremde Antworten).</summary>
    public event Action<HidppDevice, HidppMessage, long>? Notification;

    private void OnChannelMessage(HidppChannel channel, HidppMessage message, long timestamp)
    {
        if (!DeviceIndexRules.Matches(DeviceIndex, message.DeviceIndex)) return;
        Notification?.Invoke(this, message, timestamp);
    }

    /// <summary>Ruft Funktion <paramref name="function"/> des Features an Index <paramref name="featureIndex"/> auf.</summary>
    public async Task<byte[]> CallAsync(byte featureIndex, byte function, byte[]? args = null,
        CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        var request = HidppMessage.Request20(DeviceIndex, featureIndex, function, SoftwareId, args ?? [], longReport: true);
        var matcher = new Hidpp20ResponseMatcher(DeviceIndex, featureIndex, request.Address);
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var response = await Channel.TransactAsync(request, matcher, timeout ?? Timeout, cancellationToken).ConfigureAwait(false);
            return response.PayloadCopy();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Ping über Root.getProtocolVersion. HID++-1.0-Geräte antworten mit Fehler "invalid SubID".</summary>
    public async Task<ProtocolVersion> GetProtocolVersionAsync(CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        var ping = (byte)Random.Shared.Next(0x01, 0xFF);
        try
        {
            var r = await CallAsync(0x00, 0x01, [0x00, 0x00, ping], cancellationToken, timeout).ConfigureAwait(false);
            return new ProtocolVersion(r[0], r[1]);
        }
        catch (HidppErrorException e) when (e.IsHidpp10 && e.Error10 == Hidpp10Error.InvalidSubId)
        {
            return new ProtocolVersion(1, 0);
        }
    }

    /// <summary>Root.getFeature – Feature-Index nachschlagen (gecacht). null = nicht vorhanden.</summary>
    public async Task<FeatureInfo?> GetFeatureAsync(ushort featureId, CancellationToken cancellationToken = default)
    {
        if (_features.TryGetValue(featureId, out var cached)) return cached;
        var r = await CallAsync(0x00, 0x00, [(byte)(featureId >> 8), (byte)featureId], cancellationToken).ConfigureAwait(false);
        FeatureInfo? info = featureId == FeatureIds.Root || r[0] != 0
            ? new FeatureInfo(featureId, r[0], r[1], r[2])
            : null;
        _features[featureId] = info;
        return info;
    }

    /// <summary>Komplette Feature-Tabelle über FeatureSet (0x0001) inkl. Root an Index 0.</summary>
    public async Task<IReadOnlyList<FeatureInfo>> EnumerateFeaturesAsync(CancellationToken cancellationToken = default)
    {
        var featureSet = await GetFeatureAsync(FeatureIds.FeatureSet, cancellationToken).ConfigureAwait(false)
                         ?? throw new HidppException("Das Gerät hat kein FeatureSet (0x0001).");
        var count = (await CallAsync(featureSet.Index, 0x00, null, cancellationToken).ConfigureAwait(false))[0];

        var list = new List<FeatureInfo>(count + 1);
        for (var i = 0; i <= count; i++)
        {
            var r = await CallAsync(featureSet.Index, 0x01, [(byte)i], cancellationToken).ConfigureAwait(false);
            var info = new FeatureInfo((ushort)((r[0] << 8) | r[1]), (byte)i, r[2], r[3]);
            list.Add(info);
            _features[info.FeatureId] = info;
        }
        return list;
    }

    public void InvalidateFeatureCache() => _features.Clear();

    // ------------------------------------------------------------ HID++ 1.0 Register

    /// <summary>Register lesen (Short 0x81 bzw. Long 0x83).</summary>
    public async Task<byte[]> ReadRegisterAsync(byte register, bool longRegister = false, byte[]? args = null,
        CancellationToken cancellationToken = default)
    {
        var subId = longRegister ? (byte)0x83 : (byte)0x81;
        var request = HidppMessage.Short(DeviceIndex, subId, register, args ?? []);
        return await TransactRegisterAsync(request, subId, register, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Register schreiben (Short 0x80 mit 3 Byte bzw. Long 0x82 mit 16 Byte).</summary>
    public async Task<byte[]> WriteRegisterAsync(byte register, byte[] value, bool longRegister = false,
        CancellationToken cancellationToken = default)
    {
        var subId = longRegister ? (byte)0x82 : (byte)0x80;
        var request = longRegister
            ? HidppMessage.Long(DeviceIndex, subId, register, value)
            : HidppMessage.Short(DeviceIndex, subId, register, value);
        return await TransactRegisterAsync(request, subId, register, cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]> TransactRegisterAsync(HidppMessage request, byte subId, byte register, CancellationToken ct)
    {
        var matcher = new Hidpp10ResponseMatcher(DeviceIndex, subId, register);
        ThrowIfDisposed();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var response = await Channel.TransactAsync(request, matcher, Timeout, ct).ConfigureAwait(false);
            return response.PayloadCopy();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new HidppTransportException("Gerät wurde geschlossen.");
    }

    /// <remarks>
    /// Das Semaphore wird bewusst nicht freigegeben: Dispose kann von einem anderen Thread kommen, während eine Anfrage
    /// noch läuft – deren Release() würde sonst ObjectDisposedException werfen und den eigentlichen Fehler verdecken.
    /// Ohne AvailableWaitHandle hält SemaphoreSlim keine nativen Ressourcen.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Channel.MessageReceived -= OnChannelMessage;
    }
}

/// <summary>Hilfen zum Erkennen von HID++-2.0-Events (Software-ID 0).</summary>
public static class HidppEvents
{
    public static bool IsEvent(HidppMessage message, byte featureIndex) =>
        message.SubId == featureIndex && message.SoftwareId == 0 && !message.IsHidpp20Error;

    public static bool IsEvent(HidppMessage message, byte featureIndex, int eventId) =>
        IsEvent(message, featureIndex) && message.FunctionId == eventId;
}
