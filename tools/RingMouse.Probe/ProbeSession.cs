using System.Diagnostics;
using System.Globalization;
using RingMouse.HidPlusPlus;
using RingMouse.HidPlusPlus.Discovery;
using RingMouse.HidPlusPlus.Transport;
using RingMouse.HidPlusPlus.Transport.Windows;

namespace RingMouse.Probe;

/// <summary>Geöffnete Verbindung zum gewählten Gerät (Endpoint, Kanal, logisches Gerät).</summary>
internal sealed class ProbeSession : IDisposable
{
    private readonly FrameRecorder? _recorder;

    private ProbeSession(ProbeOptions options, HidppEndpoint endpoint, HidppChannel channel, HidppDevice device,
        EndpointIdentity identity, ProtocolVersion? protocol, FrameRecorder? recorder)
    {
        Options = options;
        Endpoint = endpoint;
        Channel = channel;
        Device = device;
        Identity = identity;
        Protocol = protocol;
        _recorder = recorder;
    }

    public ProbeOptions Options { get; }
    public HidppEndpoint Endpoint { get; }
    public HidppChannel Channel { get; }
    public HidppDevice Device { get; }
    public EndpointIdentity Identity { get; }
    public ProtocolVersion? Protocol { get; }

    public static IReadOnlyList<HidppEndpoint> SortEndpoints(IEnumerable<HidppEndpoint> endpoints) =>
        endpoints.OrderBy(e => e.Bus == HidBusType.Usb ? 1 : 0)
            .ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static async Task<ProbeSession> OpenAsync(ProbeOptions o, CancellationToken ct)
    {
        var transport = new WinHidTransport();
        var endpoints = SortEndpoints(HidppDiscovery.FindEndpoints(transport));
        if (endpoints.Count == 0)
            throw new ProbeException("No Logitech device with a HID++ collection found. Is the mouse connected and awake?");

        var endpoint = SelectEndpoint(endpoints, o.Device);
        HidppChannel channel;
        try
        {
            channel = endpoint.Open(transport);
        }
        catch (HidIoException ex) when (ex.NativeErrorCode is 5 or 32)
        {
            throw new ProbeException($"{ex.Message}\nIs another program holding the HID++ collection open exclusively?");
        }

        FrameRecorder? recorder = null;
        try
        {
            if (o.RecordPath is not null)
            {
                recorder = new FrameRecorder(o.RecordPath, new Dictionary<string, string>
                {
                    ["tool"] = "ringmouse-probe",
                    ["command"] = o.Command,
                    ["device"] = endpoint.DisplayName,
                    ["bus"] = endpoint.BusText,
                    ["vid"] = endpoint.VendorId.ToString("X4"),
                    ["pid"] = endpoint.ProductId.ToString("X4"),
                    ["swid"] = o.SoftwareId.ToString(CultureInfo.InvariantCulture),
                    ["recorded"] = DateTimeOffset.Now.ToString("O"),
                });
                recorder.Attach(channel);
            }
            if (o.Raw) channel.FrameTraced += PrintRawFrame;

            var identity = await HidppDiscovery.IdentifyAsync(channel, endpoint.Bus, o.SoftwareId, ct).ConfigureAwait(false);
            byte index;
            ProtocolVersion? protocol;
            if (identity.IsReceiver)
            {
                var slot = o.ReceiverIndex is { } wanted
                    ? identity.Slots.FirstOrDefault(s => s.DeviceIndex == wanted)
                      ?? throw new ProbeException($"No device responds at receiver index {wanted}.")
                    : identity.Slots.FirstOrDefault(s => s.Protocol is { IsHidpp20: true })
                      ?? throw new ProbeException("No HID++ 2.0 device is currently responding on the receiver (asleep? move the mouse).");
                index = slot.DeviceIndex;
                protocol = slot.Protocol;
            }
            else if (identity.Responds && identity.DirectProtocol is { } p)
            {
                index = HidppMessage.DirectDeviceIndex;
                protocol = p;
            }
            else
            {
                throw new ProbeException($"{endpoint.DisplayName} does not respond to HID++. Is the mouse asleep? Move it briefly and try again.");
            }

            var device = new HidppDevice(channel, index, o.SoftwareId) { Timeout = TimeSpan.FromMilliseconds(o.TimeoutMs) };
            return new ProbeSession(o, endpoint, channel, device, identity, protocol, recorder);
        }
        catch
        {
            recorder?.Dispose();
            channel.Dispose();
            throw;
        }
    }

    private static HidppEndpoint SelectEndpoint(IReadOnlyList<HidppEndpoint> endpoints, string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return endpoints[0];
        if (int.TryParse(spec, out var n) && n >= 1 && n <= endpoints.Count) return endpoints[n - 1];

        var hex = spec.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? spec[2..] : spec;
        if (ushort.TryParse(hex, NumberStyles.HexNumber, null, out var pid))
        {
            var byPid = endpoints.FirstOrDefault(e => e.ProductId == pid);
            if (byPid is not null) return byPid;
        }

        return endpoints.FirstOrDefault(e => e.DisplayName.Contains(spec, StringComparison.OrdinalIgnoreCase))
               ?? throw new ProbeException($"Device '{spec}' not found – see 'ringmouse-probe list'.");
    }

    private static void PrintRawFrame(HidppChannel channel, FrameDirection direction, byte[] frame, long timestamp) =>
        ConsoleOut.Line(direction == FrameDirection.Tx ? ConsoleColor.DarkYellow : ConsoleColor.DarkGray,
            $"  {DateTime.Now:HH:mm:ss.fff} {(direction == FrameDirection.Tx ? "TX" : "RX")} {HidppMessage.ToHex(frame)}");

    public void Dispose()
    {
        Device.Dispose();
        Channel.Dispose();
        _recorder?.Dispose();
    }
}

internal static class OptionsPlusCheck
{
    private static readonly string[] s_processNames =
        ["logioptionsplus_agent", "logioptionsplus", "LogiOptionsMgr", "LogiOptions", "LogiPluginService"];

    public static IReadOnlyList<string> Running()
    {
        var found = new List<string>();
        foreach (var name in s_processNames)
        {
            var procs = Process.GetProcessesByName(name);
            if (procs.Length > 0) found.Add(name);
            foreach (var p in procs) p.Dispose();
        }
        return found;
    }

    public static void WarnIfRunning()
    {
        var running = Running();
        if (running.Count > 0)
            ConsoleOut.Warn($"Logi Options+ is running ({string.Join(", ", running)}) – it can overwrite diversions at any time.");
    }
}
