namespace RingMouse.HidPlusPlus.Features;

/// <summary>
/// WIRELESS_DEVICE_STATUS (0x1D4B) Event 0: das Gerät meldet eine (Wieder-)Verbindung und ggf.,
/// dass die Host-Software ihre Einstellungen neu anwenden soll (Diversion geht dabei verloren).
/// </summary>
public sealed record WirelessStatusEvent(byte Status, byte Request, byte Reason)
{
    public bool IsReconnection => Status == 1;
    public bool ReconfigurationNeeded => Request == 1;
    public bool PowerSwitchActivated => Reason == 1;

    public static bool TryParse(HidppMessage message, byte featureIndex, out WirelessStatusEvent status)
    {
        status = null!;
        if (!HidppEvents.IsEvent(message, featureIndex, 0)) return false;
        var p = message.Payload;
        status = new WirelessStatusEvent(p[0], p[1], p[2]);
        return true;
    }

    public override string ToString() =>
        $"status={(IsReconnection ? "reconnect" : Status.ToString())} request={(ReconfigurationNeeded ? "reconfigure" : Request.ToString())} reason={(PowerSwitchActivated ? "power switch" : Reason.ToString())}";
}
