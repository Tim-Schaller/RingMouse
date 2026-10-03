using System.Diagnostics;
using RingMouse.HidPlusPlus.Transport;

namespace RingMouse.Device.Tests;

/// <summary>
/// Simulierte MX Vertical über Bluetooth LE: Feature-Indizes, Tasten und Verhalten wie in der echten
/// Probe-Aufzeichnung. Beantwortet HID++-2.0-Anfragen asynchron und kann Events, Schlaf, Reconnect
/// (mit Verlust der Umleitungen) und fremde Software simulieren.
/// </summary>
internal sealed class SimulatedMouse
{
    public sealed class Reporting
    {
        public bool Diverted;
        public bool Persist;
        public bool RawXY;
        public bool Analytics;
    }

    // Dichte Feature-Tabelle wie bei der echten MX Vertical (Index = Position); DPI-Feature an Index 18
    private readonly ushort[] _featureTable =
    [
        0x0000, 0x0001, 0x0003, 0x0005, 0x1D4B, 0x0020, 0x0021, 0x0007, 0x1000, 0x1002,
        0x1B04, 0x1C00, 0x1814, 0x1815, 0x2250, 0x18B1, 0x2100, 0x2130, 0x2201,
    ];

    // CID, TID, Flags1, Group, GMask, Flags2 – wie getCidInfo der echten Maus
    private static readonly (ushort Cid, ushort Tid, byte F1, byte Group, byte Mask, byte F2)[] s_controls =
    [
        (0x0050, 0x0038, 0x11, 1, 1, 0x04),
        (0x0051, 0x0039, 0x11, 1, 1, 0x04),
        (0x0052, 0x003A, 0x71, 2, 3, 0x05),
        (0x0053, 0x003C, 0x71, 2, 3, 0x05),
        (0x0056, 0x003E, 0x71, 2, 3, 0x05),
        (0x00FD, 0x00D2, 0x71, 2, 3, 0x05),
        (0x00D7, 0x00B4, 0xA0, 3, 0, 0x03),
    ];

    private readonly object _lock = new();
    private SimulatedPort? _port;

    /// <param name="extendedDpi">wie eine neuere Maus: EXTENDED_ADJUSTABLE_DPI (0x2202) statt 0x2201</param>
    public SimulatedMouse(bool extendedDpi = false)
    {
        ExtendedDpi = extendedDpi;
        if (extendedDpi) _featureTable[18] = 0x2202;
        Info = new HidDeviceInfo
        {
            Path = @"\\?\hid#sim_mx_vertical&col02",
            InstanceId = @"HID\SIM_MXV&COL02\1",
            ParentInstanceId = @"BTHLEDEVICE\SIM_MXV\1",
            Bus = HidBusType.BluetoothLe,
            VendorId = 0x046D,
            ProductId = 0xB020,
            UsagePage = 0xFF43,
            Usage = 0x0202,
            InputReportLength = 20,
            OutputReportLength = 20,
            InputReportIds = [0x11],
            OutputReportIds = [0x11],
            Product = "MX Vertical",
        };
        foreach (var c in s_controls) State[c.Cid] = new Reporting { Analytics = c.F2 != 0x03 };
        // wie mit laufendem Options+: Zurück/Vor/DPI umgeleitet
        State[0x0053].Diverted = true;
        State[0x0056].Diverted = true;
    }

    public HidDeviceInfo Info { get; }
    public bool ExtendedDpi { get; }
    public Dictionary<ushort, Reporting> State { get; } = new();
    public int Dpi { get; set; } = 1000;
    public byte Lod { get; set; } = 2;
    public int BatteryPercent { get; set; } = 20;
    public volatile bool Asleep;
    public int RequestCount;

    /// <summary>So viele der nächsten Port-Starts scheitern sofort (flatternde BLE-Verbindung).</summary>
    public int FailingStarts;

    /// <summary>So viele der nächsten Schreibvorgänge werfen eine unerwartete Ausnahme (Treiberfehler).</summary>
    public int FaultyWrites;

    internal static bool TryConsume(ref int counter)
    {
        while (true)
        {
            var current = Volatile.Read(ref counter);
            if (current <= 0) return false;
            if (Interlocked.CompareExchange(ref counter, current - 1, current) == current) return true;
        }
    }

    public IHidPort OpenPort()
    {
        lock (_lock)
        {
            _port = new SimulatedPort(this);
            return _port;
        }
    }

    public Reporting Get(ushort cid)
    {
        lock (_lock) return State[cid];
    }

    /// <summary>Tastenstatus-Event (0x1B04 Event 0) mit den aktuell gedrückten CIDs.</summary>
    public void Press(params ushort[] cids)
    {
        var payload = new byte[16];
        for (var i = 0; i < cids.Length && i < 4; i++)
        {
            payload[i * 2] = (byte)(cids[i] >> 8);
            payload[i * 2 + 1] = (byte)cids[i];
        }
        Inject(10, 0x00, payload);
    }

    public void RawXY(short dx, short dy) => Inject(10, 0x10, [(byte)(dx >> 8), (byte)dx, (byte)(dy >> 8), (byte)dy]);

    public void BatteryEvent(int percent, byte status = 0)
    {
        BatteryPercent = percent;
        Inject(8, 0x00, [(byte)percent, 0, status]);
    }

    /// <summary>Wiederverbindung wie nach dem Aufwachen: temporäre Umleitungen weg, 0x1D4B meldet "reconfigure".</summary>
    public void Reconnect()
    {
        lock (_lock)
        {
            foreach (var r in State.Values)
            {
                r.Diverted = false;
                r.RawXY = false;
            }
        }
        Inject(4, 0x00, [0x01, 0x01, 0x00]);
    }

    /// <summary>Fremde Software (Options+) setzt eine Umleitung still zurück.</summary>
    public void ForeignReset(ushort cid)
    {
        lock (_lock)
        {
            State[cid].Diverted = false;
            State[cid].RawXY = false;
        }
    }

    public void Unplug() => _port?.Fail(new IOException("Gerät entfernt"));

    private void Inject(byte featureIndex, byte address, byte[] payload)
    {
        var report = new byte[20];
        report[0] = 0x11;
        report[1] = 0xFF;
        report[2] = featureIndex;
        report[3] = address;
        payload.AsSpan(0, Math.Min(16, payload.Length)).CopyTo(report.AsSpan(4));
        _port?.Deliver(report);
    }

    internal byte[]? Handle(byte[] req)
    {
        Interlocked.Increment(ref RequestCount);
        if (Asleep) return null;
        var feature = req[2];
        var fnSw = req[3];
        var fn = fnSw >> 4;
        var p = req.AsSpan(4);
        byte[]? data;
        lock (_lock)
        {
            data = (feature, fn) switch
            {
                (0, 0) => GetFeature((ushort)((p[0] << 8) | p[1])),
                (0, 1) => [4, 5, p[2]],
                (1, 0) => [(byte)(_featureTable.Length - 1)],
                (1, 1) => p[0] < _featureTable.Length ? [(byte)(_featureTable[p[0]] >> 8), (byte)_featureTable[p[0]], 0, 0] : null,
                (2, 0) => [1, 0x1A, 0x2B, 0x3C, 0x4D, 0x00, 0x0E, 0xB0, 0x20, 0x40, 0x7B, 0xC0, 0x8A, 0x00, 0x00],
                (2, 1) => [0, (byte)'M', (byte)'P', (byte)'M', 0x16, 0x00, 0x00, 0x09, 0x01, 0xB0, 0x20],
                (3, 0) => [11],
                (3, 1) => "MX Vertical"u8.ToArray().Skip(p[0]).ToArray(),
                (3, 2) => [3],
                (8, 0) => [(byte)BatteryPercent, 10, 0],
                (10, 0) => [(byte)s_controls.Length],
                (10, 1) => p[0] < s_controls.Length ? ControlInfo(p[0]) : null,
                (10, 2) => GetReporting((ushort)((p[0] << 8) | p[1])),
                (10, 3) => SetReporting(p),
                (18, 0) => [1],
                (18, 1) when !ExtendedDpi => [0, 0x01, 0x90, 0xE0, 0x64, 0x0F, 0xA0, 0, 0],
                (18, 2) when !ExtendedDpi => [0, (byte)(Dpi >> 8), (byte)Dpi, 0x03, 0xE8],
                (18, 3) when !ExtendedDpi => SetDpi(p),
                // 0x2202: Fähigkeiten, DPI-Bereiche 200…8000 in 50er-Schritten (eine Seite), Zustand, Setzen
                (18, 1) => [p[0], 1, 0x03],
                (18, 2) => p[2] == 0 ? [p[0], p[1], 0, 0x00, 0xC8, 0xE0, 0x32, 0x1F, 0x40, 0, 0] : [p[0], p[1], p[2]],
                (18, 5) => [0, (byte)(Dpi >> 8), (byte)Dpi, 0x03, 0xE8, (byte)(Dpi >> 8), (byte)Dpi, 0x03, 0xE8, Lod],
                (18, 6) => SetExtendedDpi(p),
                _ => null,
            };
        }

        var resp = new byte[20];
        resp[0] = 0x11;
        resp[1] = 0xFF;
        if (data is null)
        {
            resp[2] = 0xFF;
            resp[3] = feature;
            resp[4] = fnSw;
            resp[5] = 0x07; // invalid function
            return resp;
        }
        resp[2] = feature;
        resp[3] = fnSw;
        data.AsSpan(0, Math.Min(16, data.Length)).CopyTo(resp.AsSpan(4));
        return resp;
    }

    private byte[] GetFeature(ushort id)
    {
        var index = Array.IndexOf(_featureTable, id);
        return index >= 0 ? [(byte)index, 0, 1] : [0, 0, 0];
    }

    private byte[] SetExtendedDpi(ReadOnlySpan<byte> p)
    {
        Dpi = (p[1] << 8) | p[2];
        Lod = p[5];
        return p[..6].ToArray();
    }

    private static byte[] ControlInfo(int i)
    {
        var c = s_controls[i];
        return [(byte)(c.Cid >> 8), (byte)c.Cid, (byte)(c.Tid >> 8), (byte)c.Tid, c.F1, 0, c.Group, c.Mask, c.F2];
    }

    private byte[]? GetReporting(ushort cid)
    {
        if (!State.TryGetValue(cid, out var r)) return null;
        var flags = (byte)((r.Diverted ? 0x01 : 0) | (r.Persist ? 0x04 : 0) | (r.RawXY ? 0x10 : 0));
        return [(byte)(cid >> 8), (byte)cid, flags, 0, 0, (byte)(r.Analytics ? 1 : 0)];
    }

    private byte[]? SetReporting(ReadOnlySpan<byte> p)
    {
        var cid = (ushort)((p[0] << 8) | p[1]);
        if (!State.TryGetValue(cid, out var r)) return null;
        var f = p[2];
        if ((f & 0x02) != 0) r.Diverted = (f & 0x01) != 0;
        if ((f & 0x08) != 0) r.Persist = (f & 0x04) != 0;
        if ((f & 0x20) != 0) r.RawXY = (f & 0x10) != 0;
        if ((p[5] & 0x02) != 0) r.Analytics = (p[5] & 0x01) != 0;
        return p[..6].ToArray();
    }

    private byte[] SetDpi(ReadOnlySpan<byte> p)
    {
        Dpi = (p[1] << 8) | p[2];
        return [0, p[1], p[2]];
    }
}

internal sealed class SimulatedPort(SimulatedMouse mouse) : IHidPort
{
    private int _closed;

    public HidDeviceInfo Info => mouse.Info;
    public bool IsOpen => Volatile.Read(ref _closed) == 0;
    public event HidReportHandler? ReportReceived;
    public event Action<IHidPort, Exception?>? Closed;

    public void Start()
    {
        if (SimulatedMouse.TryConsume(ref mouse.FailingStarts)) Fail(new IOException("Verbindung sofort wieder weg"));
    }

    public void Write(ReadOnlySpan<byte> report, TimeSpan timeout)
    {
        if (!IsOpen) throw new ObjectDisposedException(nameof(SimulatedPort));
        if (SimulatedMouse.TryConsume(ref mouse.FaultyWrites)) throw new InvalidOperationException("simulierter Treiberfehler");
        var copy = report.ToArray();
        var response = mouse.Handle(copy);
        if (response is not null)
            _ = Task.Run(async () =>
            {
                await Task.Delay(3).ConfigureAwait(false);
                Deliver(response);
            });
    }

    public void Deliver(byte[] report)
    {
        if (IsOpen) ReportReceived?.Invoke(this, report, Stopwatch.GetTimestamp());
    }

    public void Fail(Exception error)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0) Closed?.Invoke(this, error);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0) Closed?.Invoke(this, null);
    }
}

internal sealed class SimulatedTransport(SimulatedMouse mouse) : IHidTransport
{
    public volatile bool Present = true;

    /// <summary>So lange blockiert die Enumeration (hängender Treiber), 0 = sofort.</summary>
    public volatile int EnumerateDelayMs;

    public IReadOnlyList<HidDeviceInfo> Enumerate(ushort? vendorId = null)
    {
        var delay = EnumerateDelayMs;
        if (delay > 0) Thread.Sleep(delay);
        return Present ? [mouse.Info] : [];
    }

    public IHidPort Open(HidDeviceInfo device) => Present ? mouse.OpenPort() : throw new IOException("nicht vorhanden");
}
