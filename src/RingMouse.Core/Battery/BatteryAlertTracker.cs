namespace RingMouse.Core.Battery;

public abstract record BatteryAlert
{
    public sealed record Low(int Threshold, int Percent) : BatteryAlert;
    public sealed record Charged(int Percent) : BatteryAlert;
}

/// <summary>Persistierbarer Zustand der Akku-Warnungen eines Geräts.</summary>
public sealed class BatteryAlertState
{
    public List<int> FiredThresholds { get; set; } = [];
    public bool ChargedNotified { get; set; }
    public int? LastPercent { get; set; }
    public bool LastCharging { get; set; }
}

/// <summary>
/// Entscheidet, wann eine Akku-Meldung fällig ist: jede Schwelle höchstens einmal pro Entladezyklus
/// (erneut scharf beim Laden oder wenn der Stand um die Hysterese über die Schwelle steigt),
/// "Aufladen abgeschlossen" einmal pro Ladezyklus.
/// </summary>
public sealed class BatteryAlertTracker
{
    private readonly int[] _thresholds;
    private readonly int _hysteresis;

    public BatteryAlertTracker(IEnumerable<int> thresholds, BatteryAlertState? state = null, int hysteresis = 5)
    {
        _thresholds = thresholds.Where(t => t is > 0 and < 100).Distinct().OrderByDescending(t => t).ToArray();
        _hysteresis = hysteresis;
        State = state ?? new BatteryAlertState();
    }

    public BatteryAlertState State { get; }

    /// <param name="percent">Ladestand oder null, wenn unbekannt.</param>
    /// <param name="charging">Gerät lädt (oder hängt am Netzteil).</param>
    /// <param name="full">Gerät meldet "voll geladen".</param>
    public BatteryAlert? Update(int? percent, bool charging, bool full)
    {
        BatteryAlert? alert = null;

        if (charging || full)
        {
            State.FiredThresholds.Clear();
            if (full && !State.ChargedNotified)
            {
                State.ChargedNotified = true;
                alert = new BatteryAlert.Charged(percent ?? 100);
            }
        }
        else
        {
            State.ChargedNotified = false;
            if (percent is { } p)
            {
                // Erneut scharf schalten, wenn der Stand deutlich über eine ausgelöste Schwelle gestiegen ist.
                State.FiredThresholds.RemoveAll(t => p >= t + _hysteresis);

                var crossed = _thresholds.Where(t => p <= t && !State.FiredThresholds.Contains(t)).ToList();
                if (crossed.Count > 0)
                {
                    // Bei einem Sprung über mehrere Schwellen nur die niedrigste melden.
                    var lowest = crossed.Min();
                    State.FiredThresholds.AddRange(crossed);
                    alert = new BatteryAlert.Low(lowest, p);
                }
            }
        }

        State.LastPercent = percent ?? State.LastPercent;
        State.LastCharging = charging;
        return alert;
    }
}
