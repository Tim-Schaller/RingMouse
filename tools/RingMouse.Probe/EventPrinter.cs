using RingMouse.HidPlusPlus;
using RingMouse.HidPlusPlus.Features;
using RingMouse.HidPlusPlus.Receivers;

namespace RingMouse.Probe;

/// <summary>Gibt eingehende HID++-Reports roh und dekodiert aus.</summary>
internal sealed class EventPrinter
{
    private readonly Dictionary<byte, FeatureInfo> _byIndex;
    private readonly Dictionary<ushort, ControlInfo> _controls;
    private readonly byte _ownSoftwareId;
    private readonly HashSet<ushort> _pressed = [];
    private readonly object _lock = new();
    private long _rawXYCount;
    private int _rawDx, _rawDy;

    public EventPrinter(IEnumerable<FeatureInfo> features, IEnumerable<ControlInfo> controls, byte ownSoftwareId)
    {
        _byIndex = features.GroupBy(f => f.Index).ToDictionary(g => g.Key, g => g.First());
        _controls = controls.GroupBy(c => c.ControlId).ToDictionary(g => g.Key, g => g.First());
        _ownSoftwareId = ownSoftwareId;
    }

    public void Print(HidppMessage m)
    {
        lock (_lock)
        {
            var (text, color) = Decode(m);
            if (text is null) return;
            var time = DateTime.Now.ToString("HH:mm:ss.fff");
            ConsoleOut.Line(ConsoleColor.DarkGray, $"{time}  RX {m}");
            ConsoleOut.Line(color, $"{new string(' ', time.Length)}  → {text}");
        }
    }

    private (string? Text, ConsoleColor Color) Decode(HidppMessage m)
    {
        if (m.IsHidpp20Error)
            return ($"HID++-2.0-Fehler {(Hidpp20Error)m.Payload[1]} auf Feature-Index {m.Address} Fn/SW 0x{m.Payload[0]:X2} (Antwort an anderes Programm?)", ConsoleColor.DarkYellow);
        if (m.IsHidpp10Error)
            return ($"HID++-1.0-Fehler {(Hidpp10Error)m.Payload[1]} auf Sub-ID 0x{m.Address:X2} Register 0x{m.Payload[0]:X2}", ConsoleColor.DarkYellow);
        if (HidppReceiver.TryParseConnectionEvent(m, out var connection))
            return ($"Receiver: {connection}", ConsoleColor.Cyan);

        if (!_byIndex.TryGetValue(m.SubId, out var feature))
            return ($"unbekannter Feature-Index {m.SubId}", ConsoleColor.Gray);

        if (m.SoftwareId != 0)
        {
            var who = m.SoftwareId == _ownSoftwareId ? "eigene, verspätet" : "fremde – anderes Programm aktiv?";
            return ($"Antwort {feature.Name} Funktion {m.FunctionId} (SW-ID {m.SoftwareId}: {who})", ConsoleColor.DarkYellow);
        }

        switch (feature.FeatureId)
        {
            case FeatureIds.ReprogControlsV4 when ReprogControlsV4Feature.TryParseDivertedButtons(m, feature.Index, out var pressed):
                return (DescribeButtons(pressed), ConsoleColor.Green);

            case FeatureIds.ReprogControlsV4 when ReprogControlsV4Feature.TryParseRawXY(m, feature.Index, out var dx, out var dy):
                _rawXYCount++;
                _rawDx += dx;
                _rawDy += dy;
                return ($"Raw-XY dx={dx,4} dy={dy,4}   (Summe {_rawDx}, {_rawDy} aus {_rawXYCount} Events)", ConsoleColor.Magenta);

            case FeatureIds.ReprogControlsV4 when ReprogControlsV4Feature.TryParseAnalytics(m, feature.Index, out var analytics):
                return ("Analytics-Key-Event (von Options+ aktiviert): " +
                        string.Join(", ", analytics.Select(a => $"{Name(a.ControlId)} {(a.Event == 1 ? "gedrückt" : "losgelassen")}")),
                    ConsoleColor.DarkCyan);

            case FeatureIds.UnifiedBattery when m.FunctionId == 0:
                return ($"Akku-Event: {Describe(BatteryDecoder.DecodeUnified(m.Payload))}", ConsoleColor.Cyan);

            case FeatureIds.BatteryVoltage when m.FunctionId == 0:
                return ($"Akku-Event: {Describe(BatteryDecoder.DecodeVoltage(m.Payload))}", ConsoleColor.Cyan);

            case FeatureIds.BatteryStatus when m.FunctionId == 0:
                return ($"Akku-Event: {Describe(BatteryDecoder.DecodeStatus(m.Payload))}", ConsoleColor.Cyan);

            case FeatureIds.WirelessDeviceStatus when WirelessStatusEvent.TryParse(m, feature.Index, out var ws):
                return ($"Wireless-Status: {ws}", ConsoleColor.Yellow);

            default:
                return ($"Event {feature.Name} (0x{feature.FeatureId:X4}) #{m.FunctionId}: {HidppMessage.ToHex(m.Payload)}", ConsoleColor.Gray);
        }
    }

    private string DescribeButtons(ushort[] pressed)
    {
        var now = pressed.ToHashSet();
        var down = now.Except(_pressed).ToList();
        var up = _pressed.Except(now).ToList();
        _pressed.Clear();
        _pressed.UnionWith(now);

        var parts = new List<string>();
        if (down.Count > 0) parts.Add("GEDRÜCKT " + string.Join(", ", down.Select(Name)));
        if (up.Count > 0) parts.Add("losgelassen " + string.Join(", ", up.Select(Name)));
        if (parts.Count == 0) parts.Add(now.Count == 0 ? "alle Tasten losgelassen" : "gedrückt: " + string.Join(", ", now.Select(Name)));
        return string.Join(" · ", parts);
    }

    private string Name(ushort cid) =>
        $"{ControlIds.Format(cid)} {(_controls.TryGetValue(cid, out var c) ? c.Name : ControlIds.GetName(cid))}";

    private static string Describe(BatteryReading r) =>
        $"{(r.Percent is { } p ? $"{p} %" : r.Level.ToString())} · {Commands.DescribeState(r.State)}" +
        (r.VoltageMillivolts is { } mv ? $" · {mv} mV" : "");
}
