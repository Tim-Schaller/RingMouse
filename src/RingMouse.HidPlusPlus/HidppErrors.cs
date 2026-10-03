namespace RingMouse.HidPlusPlus;

/// <summary>Fehlercodes einer HID++-2.0-Fehlerantwort (Feature-Index 0xFF).</summary>
public enum Hidpp20Error : byte
{
    NoError = 0,
    Unknown = 1,
    InvalidArgument = 2,
    OutOfRange = 3,
    HardwareError = 4,
    LogitechInternal = 5,
    InvalidFeatureIndex = 6,
    InvalidFunctionId = 7,
    Busy = 8,
    Unsupported = 9,
}

/// <summary>Fehlercodes einer HID++-1.0-Fehlerantwort (Sub-ID 0x8F).</summary>
public enum Hidpp10Error : byte
{
    Success = 0x00,
    InvalidSubId = 0x01,
    InvalidAddress = 0x02,
    InvalidValue = 0x03,
    ConnectFail = 0x04,
    TooManyDevices = 0x05,
    AlreadyExists = 0x06,
    Busy = 0x07,
    UnknownDevice = 0x08,
    ResourceError = 0x09,
    RequestUnavailable = 0x0A,
    InvalidParamValue = 0x0B,
    WrongPinCode = 0x0C,
}

public class HidppException : Exception
{
    public HidppException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Das Gerät hat mit einer Fehlermeldung geantwortet.</summary>
public sealed class HidppErrorException : HidppException
{
    public HidppErrorException(bool isHidpp10, byte errorCode, HidppMessage request)
        : base(Describe(isHidpp10, errorCode, request))
    {
        IsHidpp10 = isHidpp10;
        ErrorCode = errorCode;
        Request = request;
    }

    public bool IsHidpp10 { get; }
    public byte ErrorCode { get; }
    public HidppMessage Request { get; }

    public Hidpp20Error? Error20 => IsHidpp10 ? null : (Hidpp20Error)ErrorCode;
    public Hidpp10Error? Error10 => IsHidpp10 ? (Hidpp10Error)ErrorCode : null;

    private static string Describe(bool v10, byte code, HidppMessage req) => v10
        ? $"HID++ 1.0 Fehler {(Hidpp10Error)code} (0x{code:X2}) auf [{req}]"
        : $"HID++ 2.0 Fehler {(Hidpp20Error)code} (0x{code:X2}) auf [{req}]";
}

/// <summary>Keine passende Antwort innerhalb des Timeouts (Gerät schläft, ist getrennt oder antwortet nicht).</summary>
public sealed class HidppTimeoutException : HidppException
{
    public HidppTimeoutException(HidppMessage request, TimeSpan timeout)
        : base($"Keine Antwort nach {timeout.TotalMilliseconds:0} ms auf [{request}]")
    {
        Request = request;
    }

    public HidppMessage Request { get; }
}

/// <summary>Transportfehler (Schreiben fehlgeschlagen, Gerät entfernt, Kanal geschlossen).</summary>
public sealed class HidppTransportException : HidppException
{
    public HidppTransportException(string message, Exception? inner = null) : base(message, inner) { }

    /// <summary>true, wenn das Gerät physisch weg ist bzw. der Kanal geschlossen wurde.</summary>
    public bool IsDeviceGone => InnerException is Transport.HidIoException { IsDeviceGone: true } or ObjectDisposedException;
}
