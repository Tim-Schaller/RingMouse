namespace RingMouse.HidPlusPlus;

public enum MatchKind
{
    None,
    Response,
    Error,
}

/// <summary>Ordnet eingehende Reports einer offenen Anfrage zu.</summary>
public interface IResponseMatcher
{
    MatchKind Match(HidppMessage message, out byte errorCode, out bool isHidpp10);
}

internal static class DeviceIndexRules
{
    /// <summary>Direkt verbundene Geräte antworten auf 0xFF; manche Firmware meldet sich mit 0x00.</summary>
    public static bool Matches(byte requested, byte received) =>
        requested == received || (requested == HidppMessage.DirectDeviceIndex && received == 0x00);
}

/// <summary>Matcher für HID++-2.0-Anfragen (Feature-Index + Funktion/Software-ID).</summary>
public sealed class Hidpp20ResponseMatcher(byte deviceIndex, byte featureIndex, byte address) : IResponseMatcher
{
    public MatchKind Match(HidppMessage m, out byte errorCode, out bool isHidpp10)
    {
        errorCode = 0;
        isHidpp10 = false;
        if (!DeviceIndexRules.Matches(deviceIndex, m.DeviceIndex)) return MatchKind.None;

        if (m.SubId == featureIndex && m.Address == address) return MatchKind.Response;

        // Fehlerformat: [Report][Index][0xFF bzw. 0x8F][Feature-Index der Anfrage][Fn/SW der Anfrage][Fehlercode]
        var p = m.Payload;
        if (m.Address == featureIndex && p.Length >= 2 && p[0] == address)
        {
            if (m.SubId == HidppMessage.Hidpp20ErrorSubId)
            {
                errorCode = p[1];
                return MatchKind.Error;
            }
            if (m.SubId == HidppMessage.Hidpp10ErrorSubId)
            {
                // HID++-1.0-Geräte/Receiver beantworten 2.0-Anfragen mit einem 1.0-Fehler.
                errorCode = p[1];
                isHidpp10 = true;
                return MatchKind.Error;
            }
        }
        return MatchKind.None;
    }
}

/// <summary>Matcher für HID++-1.0-Registerzugriffe (Sub-ID 0x80–0x83 + Register).</summary>
public sealed class Hidpp10ResponseMatcher(byte deviceIndex, byte subId, byte register) : IResponseMatcher
{
    public MatchKind Match(HidppMessage m, out byte errorCode, out bool isHidpp10)
    {
        errorCode = 0;
        isHidpp10 = true;
        if (!DeviceIndexRules.Matches(deviceIndex, m.DeviceIndex)) return MatchKind.None;
        if (m.SubId == subId && m.Address == register) return MatchKind.Response;

        var p = m.Payload;
        if (m.SubId == HidppMessage.Hidpp10ErrorSubId && m.Address == subId && p.Length >= 2 && p[0] == register)
        {
            errorCode = p[1];
            return MatchKind.Error;
        }
        return MatchKind.None;
    }
}
