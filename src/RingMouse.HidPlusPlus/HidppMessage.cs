using System.Globalization;
using System.Text;

namespace RingMouse.HidPlusPlus;

/// <summary>
/// Ein HID++-Report.
/// Layout: [ReportId][DeviceIndex][SubId/FeatureIndex][Address/FunctionSw][Payload...]
/// HID++ 2.0: SubId = Feature-Index, Address = (Funktion &lt;&lt; 4) | Software-ID.
/// </summary>
public sealed class HidppMessage
{
    public const byte ShortReportId = 0x10;
    public const byte LongReportId = 0x11;
    public const byte VeryLongReportId = 0x12;

    public const int ShortLength = 7;
    public const int LongLength = 20;
    public const int VeryLongLength = 64;

    public const int ShortPayloadLength = ShortLength - 4;
    public const int LongPayloadLength = LongLength - 4;
    public const int VeryLongPayloadLength = VeryLongLength - 4;

    /// <summary>Feature-Index 0xFF markiert eine HID++-2.0-Fehlerantwort.</summary>
    public const byte Hidpp20ErrorSubId = 0xFF;
    /// <summary>Sub-ID 0x8F markiert eine HID++-1.0-Fehlerantwort.</summary>
    public const byte Hidpp10ErrorSubId = 0x8F;

    /// <summary>Device-Index für direkt verbundene Geräte (Bluetooth/USB-Kabel) bzw. den Receiver selbst.</summary>
    public const byte DirectDeviceIndex = 0xFF;

    private readonly byte[] _payload;

    public HidppMessage(byte reportId, byte deviceIndex, byte subId, byte address, ReadOnlySpan<byte> payload)
    {
        var len = PayloadLengthFor(reportId);
        if (len < 0) throw new ArgumentOutOfRangeException(nameof(reportId), $"Not a HID++ report ID: 0x{reportId:X2}");
        if (payload.Length > len)
            throw new ArgumentException($"Payload ({payload.Length}) too long for report 0x{reportId:X2} (max. {len}).", nameof(payload));

        ReportId = reportId;
        DeviceIndex = deviceIndex;
        SubId = subId;
        Address = address;
        _payload = new byte[len];
        payload.CopyTo(_payload);
    }

    public byte ReportId { get; }
    public byte DeviceIndex { get; }
    public byte SubId { get; }
    public byte Address { get; }

    /// <summary>Nutzdaten nach den ersten vier Bytes (bei Short 3, bei Long 16 Byte).</summary>
    public ReadOnlySpan<byte> Payload => _payload;

    public byte[] PayloadCopy() => (byte[])_payload.Clone();

    public bool IsShort => ReportId == ShortReportId;
    public bool IsLong => ReportId == LongReportId;

    public byte FeatureIndex => SubId;
    public byte FunctionId => (byte)(Address >> 4);
    public byte SoftwareId => (byte)(Address & 0x0F);

    public bool IsHidpp20Error => SubId == Hidpp20ErrorSubId;
    public bool IsHidpp10Error => SubId == Hidpp10ErrorSubId;

    /// <summary>true, wenn die Nutzdaten ab Byte 3 null sind und die Nachricht als Short-Report passt.</summary>
    public bool FitsShort
    {
        get
        {
            for (var i = ShortPayloadLength; i < _payload.Length; i++)
                if (_payload[i] != 0) return false;
            return true;
        }
    }

    public static HidppMessage Short(byte deviceIndex, byte subId, byte address, ReadOnlySpan<byte> payload = default) =>
        new(ShortReportId, deviceIndex, subId, address, payload);

    public static HidppMessage Long(byte deviceIndex, byte subId, byte address, ReadOnlySpan<byte> payload = default) =>
        new(LongReportId, deviceIndex, subId, address, payload);

    /// <summary>HID++-2.0-Request: Feature-Index, Funktion (0–15), Software-ID (1–15).</summary>
    public static HidppMessage Request20(byte deviceIndex, byte featureIndex, byte function, byte softwareId,
        ReadOnlySpan<byte> args = default, bool longReport = true)
    {
        if (function > 0x0F) throw new ArgumentOutOfRangeException(nameof(function));
        if (softwareId is 0 or > 0x0F) throw new ArgumentOutOfRangeException(nameof(softwareId), "Software ID must be 1–15.");
        var address = (byte)((function << 4) | softwareId);
        return longReport || args.Length > ShortPayloadLength
            ? Long(deviceIndex, featureIndex, address, args)
            : Short(deviceIndex, featureIndex, address, args);
    }

    public HidppMessage ToLong() => IsLong ? this : new HidppMessage(LongReportId, DeviceIndex, SubId, Address, _payload);

    public HidppMessage ToShort()
    {
        if (IsShort) return this;
        if (!FitsShort) throw new InvalidOperationException("Message does not fit into a short report.");
        return new HidppMessage(ShortReportId, DeviceIndex, SubId, Address, _payload.AsSpan(0, ShortPayloadLength));
    }

    /// <summary>Vollständiger Report inkl. Report-ID.</summary>
    public byte[] ToArray()
    {
        var bytes = new byte[4 + _payload.Length];
        bytes[0] = ReportId;
        bytes[1] = DeviceIndex;
        bytes[2] = SubId;
        bytes[3] = Address;
        _payload.CopyTo(bytes, 4);
        return bytes;
    }

    /// <summary>Parst einen rohen Input-Report. Unbekannte Report-IDs (z.B. DJ 0x20) werden abgelehnt.</summary>
    public static bool TryParse(ReadOnlySpan<byte> report, out HidppMessage message)
    {
        message = null!;
        if (report.Length < 4) return false;
        var len = PayloadLengthFor(report[0]);
        if (len < 0) return false;
        var payload = report[4..];
        if (payload.Length > len) payload = payload[..len];
        message = new HidppMessage(report[0], report[1], report[2], report[3], payload);
        return true;
    }

    public static HidppMessage Parse(string hex)
    {
        var bytes = ParseHex(hex);
        return TryParse(bytes, out var m) ? m : throw new FormatException($"Not a HID++ report: {hex}");
    }

    public static int PayloadLengthFor(byte reportId) => reportId switch
    {
        ShortReportId => ShortPayloadLength,
        LongReportId => LongPayloadLength,
        VeryLongReportId => VeryLongPayloadLength,
        _ => -1,
    };

    public override string ToString() => ToHex(ToArray());

    public static string ToHex(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder(data.Length * 3);
        for (var i = 0; i < data.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(data[i].ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    public static byte[] ParseHex(string hex)
    {
        var clean = new StringBuilder(hex.Length);
        foreach (var c in hex)
            if (Uri.IsHexDigit(c)) clean.Append(c);
        if (clean.Length % 2 != 0) throw new FormatException("Odd number of hex digits.");
        return Convert.FromHexString(clean.ToString());
    }
}
