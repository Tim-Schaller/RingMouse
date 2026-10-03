namespace RingMouse.HidPlusPlus.Transport;

/// <summary>Callback für einen empfangenen Input-Report (inkl. Report-ID) mit Stopwatch-Zeitstempel.</summary>
public delegate void HidReportHandler(IHidPort port, byte[] report, long timestamp);

/// <summary>Abstraktion über den HID-Zugriff, damit Protokoll und Transport getrennt bleiben.</summary>
public interface IHidTransport
{
    /// <summary>Listet alle HID-Collections (optional gefiltert nach Vendor-ID).</summary>
    IReadOnlyList<HidDeviceInfo> Enumerate(ushort? vendorId = null);

    /// <summary>Öffnet eine Collection für Lesen und Schreiben. Wirft <see cref="IOException"/> bei Fehlern.</summary>
    IHidPort Open(HidDeviceInfo device);
}

/// <summary>Eine geöffnete HID-Collection.</summary>
public interface IHidPort : IDisposable
{
    HidDeviceInfo Info { get; }

    /// <summary>Wird auf dem Lese-Thread ausgelöst – Handler müssen schnell zurückkehren.</summary>
    event HidReportHandler? ReportReceived;

    /// <summary>Lese-Schleife beendet (Gerät weg, Fehler oder Dispose). Exception ist null bei regulärem Dispose.</summary>
    event Action<IHidPort, Exception?>? Closed;

    bool IsOpen { get; }

    /// <summary>Startet den Lese-Thread (idempotent).</summary>
    void Start();

    /// <summary>Schreibt einen Output-Report (erstes Byte = Report-ID). Wird auf die Output-Report-Länge aufgefüllt.</summary>
    void Write(ReadOnlySpan<byte> report, TimeSpan timeout);
}

/// <summary>Meldet An- und Abmeldung von HID-Interfaces.</summary>
public interface IHidDeviceWatcher : IDisposable
{
    event Action<string>? InterfaceArrived;
    event Action<string>? InterfaceRemoved;
    void Start();
}

/// <summary>I/O-Fehler mit Win32-Fehlercode.</summary>
public sealed class HidIoException : IOException
{
    public int NativeErrorCode { get; }

    public HidIoException(int nativeError, string operation)
        : base($"{operation} failed: Win32 error {nativeError} ({DescribeError(nativeError)})")
    {
        NativeErrorCode = nativeError;
        HResult = unchecked((int)0x80070000) | (nativeError & 0xFFFF);
    }

    /// <summary>true, wenn der Fehler bedeutet, dass das Gerät nicht (mehr) erreichbar ist.</summary>
    public bool IsDeviceGone => NativeErrorCode is 1167 /*DEVICE_NOT_CONNECTED*/ or 2 /*FILE_NOT_FOUND*/
        or 6 /*INVALID_HANDLE*/ or 21 /*NOT_READY*/ or 31 /*GEN_FAILURE*/ or 55 /*DEV_NOT_EXIST*/
        or 1784 /*INVALID_USER_BUFFER*/ or 995 /*OPERATION_ABORTED*/;

    private static string DescribeError(int code) => code switch
    {
        2 => "file/device not found",
        5 => "access denied",
        6 => "invalid handle",
        21 => "device not ready",
        31 => "device not functioning (GEN_FAILURE)",
        32 => "sharing violation (another process holds the device exclusively)",
        55 => "device no longer exists",
        87 => "invalid parameter",
        995 => "operation aborted",
        1167 => "device not connected",
        _ => new System.ComponentModel.Win32Exception(code).Message,
    };
}
