namespace RingMouse.HidPlusPlus.Transport;

/// <summary>Physische Anbindung eines HID-Geräts, abgeleitet aus der Instanz-ID.</summary>
public enum HidBusType
{
    Unknown,
    Usb,
    BluetoothLe,
    BluetoothClassic,
}

/// <summary>
/// Beschreibt genau eine HID-Top-Level-Collection (unter Windows ein eigenes Device-Interface
/// mit eigenem Pfad, z.B. ...&amp;COL02).
/// </summary>
public sealed record HidDeviceInfo
{
    public required string Path { get; init; }
    public string InstanceId { get; init; } = "";
    /// <summary>Instanz-ID des Elternknotens; Collections desselben physischen Geräts teilen ihn.</summary>
    public string ParentInstanceId { get; init; } = "";
    public HidBusType Bus { get; init; }

    public ushort VendorId { get; init; }
    public ushort ProductId { get; init; }
    public ushort VersionNumber { get; init; }

    public ushort UsagePage { get; init; }
    public ushort Usage { get; init; }

    /// <summary>Länge inkl. Report-ID-Byte (so wie Windows sie meldet).</summary>
    public int InputReportLength { get; init; }
    public int OutputReportLength { get; init; }
    public int FeatureReportLength { get; init; }

    public IReadOnlyList<byte> InputReportIds { get; init; } = [];
    public IReadOnlyList<byte> OutputReportIds { get; init; } = [];
    public IReadOnlyList<byte> FeatureReportIds { get; init; } = [];

    public string? Manufacturer { get; init; }
    public string? Product { get; init; }
    public string? SerialNumber { get; init; }

    /// <summary>true, wenn die Collection nur zum Abfragen (ohne Lese-/Schreibrecht) geöffnet werden konnte.</summary>
    public bool QueryOnly { get; init; }

    public bool IsVendorDefined => UsagePage >= 0xFF00;

    /// <summary>Kurzbezeichnung der Collection aus dem Pfad (z.B. "COL02"), sonst leer.</summary>
    public string CollectionTag
    {
        get
        {
            var idx = InstanceId.LastIndexOf("&COL", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return "";
            var end = InstanceId.IndexOf('\\', idx);
            return (end < 0 ? InstanceId[(idx + 1)..] : InstanceId[(idx + 1)..end]).ToUpperInvariant();
        }
    }

    public override string ToString() =>
        $"{VendorId:X4}:{ProductId:X4} UP 0x{UsagePage:X4} U 0x{Usage:X4} in {InputReportLength} out {OutputReportLength} {CollectionTag}";
}
