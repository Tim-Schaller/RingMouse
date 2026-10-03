using RingMouse.Core.Config;

namespace RingMouse.Core.Ring;

public enum RingPhase
{
    /// <summary>Kein Ring sichtbar.</summary>
    Closed,
    /// <summary>Auslösetaste wird gehalten – Loslassen wählt aus.</summary>
    Holding,
    /// <summary>Ring bleibt offen – Klick wählt aus, Esc/Klick außerhalb/Taste erneut bricht ab.</summary>
    Open,
}

public enum RingCloseReason
{
    Cancelled,
    ReleasedInCenter,
    ClickedOutside,
    Escape,
    TriggerPressedAgain,
    Timeout,
    Superseded,
    Error,
}

public sealed record RingInteractionOptions(
    RingMode Mode,
    double Deadzone,
    double OuterRadius,
    int TapThresholdMs,
    bool AllowTapMode = true,
    int AutoCloseMs = 0,
    double SubmenuPushRadius = 0)
{
    /// <summary>Klicks weiter als Außenradius × Faktor gelten als "außerhalb".</summary>
    public const double OutsideFactor = 1.12;

    /// <summary>Anteil des Außenradius, ab dem Weiterschieben beim Halten ein Untermenü öffnet.</summary>
    public const double SubmenuPushFactor = 0.85;
}

/// <summary>Anweisung an die Oberfläche nach einem Eingabe-Ereignis.</summary>
public abstract record RingCommand
{
    public static readonly RingCommand None = new NoneCommand();

    public sealed record NoneCommand : RingCommand;
    public sealed record Show(string Name, RingDefinition Ring) : RingCommand;
    public sealed record Highlight(int Index) : RingCommand;
    public sealed record SwitchRing(string Name, RingDefinition Ring, bool IsSubmenu) : RingCommand;
    public sealed record Execute(RingSegment Segment, string RingName, int Index) : RingCommand;
    public sealed record Close(RingCloseReason Reason) : RingCommand;
}

/// <summary>
/// Zustandsautomat des Actions Rings – reine Logik, ohne UI. Die Oberfläche liefert Ereignisse
/// (Taste gedrückt/losgelassen, Zeigerversatz relativ zur Ringmitte in DIP, Klicks, Esc, Zeit) und setzt
/// die zurückgegebenen Befehle um.
/// </summary>
public sealed class RingInteraction
{
    public const int MaxSubmenuDepth = 4;

    /// <summary>Ein markiertes Segment bleibt bis so viele Grad hinter seiner Kante markiert – kein Flackern auf der Grenze.</summary>
    public const double HysteresisDegrees = 5;

    /// <summary>Ab diesem Anteil des Durchschiebe-Radius beginnt die Anzeige des Fortschritts.</summary>
    public const double PushProgressStart = 0.6;

    private readonly Func<string, RingDefinition?> _lookup;
    private readonly Stack<(string Name, RingDefinition Ring)> _stack = new();
    private long _phaseStartedMs;
    private long _lastActivityMs;
    private bool _leftDeadzone;

    public RingInteraction(Func<string, RingDefinition?> ringLookup)
    {
        _lookup = ringLookup;
        Options = new RingInteractionOptions(RingMode.Hybrid, 26, 150, 350);
    }

    public RingPhase Phase { get; private set; } = RingPhase.Closed;
    public ushort Trigger { get; private set; }
    public int Highlighted { get; private set; } = -1;
    public RingInteractionOptions Options { get; private set; }
    public string? CurrentName => _stack.Count > 0 ? _stack.Peek().Name : null;
    public RingDefinition? Current => _stack.Count > 0 ? _stack.Peek().Ring : null;
    public bool IsSubmenu => _stack.Count > 1;
    public int Depth => _stack.Count;
    public bool IsOpen => Phase != RingPhase.Closed;

    public RingCommand Open(string ringName, ushort trigger, long nowMs, RingInteractionOptions options)
    {
        var ring = _lookup(ringName);
        if (ring is null || ring.Segments.Count == 0) return RingCommand.None;

        _stack.Clear();
        _stack.Push((ringName, ring));
        Options = options;
        Trigger = trigger;
        Highlighted = -1;
        _leftDeadzone = false;
        _phaseStartedMs = _lastActivityMs = nowMs;
        Phase = options.Mode == RingMode.Tap && options.AllowTapMode ? RingPhase.Open : RingPhase.Holding;
        return new RingCommand.Show(ringName, ring);
    }

    public RingCommand PointerMoved(double dx, double dy, long nowMs)
    {
        if (Phase == RingPhase.Closed) return RingCommand.None;
        var distance = RingGeometry.Distance(dx, dy);
        if (distance >= Options.Deadzone)
        {
            _leftDeadzone = true;
        }
        var index = TrackedIndexAt(dx, dy);

        // Halten und über ein Untermenü hinaus weiterschieben: Untermenü öffnen, Taste bleibt gedrückt –
        // Loslassen wählt dann dort aus (eine durchgehende Geste statt Loslassen + Klicken).
        if (Phase == RingPhase.Holding && Options.SubmenuPushRadius > 0 && distance >= Options.SubmenuPushRadius &&
            index >= 0 && Current!.Segments[index]!.Action is SubmenuAction)
        {
            var switched = EnterSubmenu(index, nowMs, keepHolding: true);
            if (switched is not RingCommand.NoneCommand) return switched;
        }

        if (index == Highlighted) return RingCommand.None;
        Highlighted = index;
        _lastActivityMs = nowMs;
        return new RingCommand.Highlight(index);
    }

    public RingCommand TriggerPressed(ushort control, long nowMs)
    {
        if (Phase != RingPhase.Open || control != Trigger) return RingCommand.None;
        if (!Options.AllowTapMode)
        {
            // Ohne Klick-Erfassung (z.B. Admin-Fenster im Vordergrund): erneutes Halten wählt im offenen Ring.
            Phase = RingPhase.Holding;
            _phaseStartedMs = _lastActivityMs = nowMs;
            _leftDeadzone = false;
            return RingCommand.None;
        }
        return CloseWith(RingCloseReason.TriggerPressedAgain);
    }

    public RingCommand TriggerReleased(ushort control, double dx, double dy, long nowMs)
    {
        if (Phase != RingPhase.Holding || control != Trigger) return RingCommand.None;

        var index = TrackedIndexAt(dx, dy);
        if (index >= 0) return Activate(index, nowMs);

        var quickTap = nowMs - _phaseStartedMs <= Options.TapThresholdMs && !_leftDeadzone;
        var staysOpen = Options.AllowTapMode && (Options.Mode == RingMode.Tap || (Options.Mode == RingMode.Hybrid && quickTap));
        if (staysOpen || (IsSubmenu && !Options.AllowTapMode))
        {
            Phase = RingPhase.Open;
            _phaseStartedMs = _lastActivityMs = nowMs;
            return RingCommand.None;
        }
        return CloseWith(RingCloseReason.ReleasedInCenter);
    }

    public RingCommand Click(double dx, double dy, bool primary, long nowMs)
    {
        if (Phase == RingPhase.Closed) return RingCommand.None;
        _lastActivityMs = nowMs;
        if (!primary) return CloseWith(RingCloseReason.Cancelled);

        var distance = RingGeometry.Distance(dx, dy);
        if (distance < Options.Deadzone) return IsSubmenu ? Back() : CloseWith(RingCloseReason.Cancelled);
        if (distance > Options.OuterRadius * RingInteractionOptions.OutsideFactor) return CloseWith(RingCloseReason.ClickedOutside);

        var index = TrackedIndexAt(dx, dy);
        return index >= 0 ? Activate(index, nowMs) : RingCommand.None;
    }

    public RingCommand Escape() => Phase == RingPhase.Closed ? RingCommand.None : CloseWith(RingCloseReason.Escape);

    /// <summary>Eine Ebene zurück (im Untermenü) bzw. schließen.</summary>
    public RingCommand Back()
    {
        if (Phase == RingPhase.Closed) return RingCommand.None;
        if (_stack.Count <= 1) return CloseWith(RingCloseReason.Cancelled);
        _stack.Pop();
        Highlighted = -1;
        var (name, ring) = _stack.Peek();
        return new RingCommand.SwitchRing(name, ring, IsSubmenu);
    }

    public RingCommand Tick(long nowMs)
    {
        if (Phase != RingPhase.Open || Options.AutoCloseMs <= 0) return RingCommand.None;
        return nowMs - _lastActivityMs > Options.AutoCloseMs ? CloseWith(RingCloseReason.Timeout) : RingCommand.None;
    }

    public RingCommand Cancel(RingCloseReason reason = RingCloseReason.Cancelled) =>
        Phase == RingPhase.Closed ? RingCommand.None : CloseWith(reason);

    /// <summary>
    /// 0–1: wie weit der Zeiger beim Halten über einem Untermenü schon Richtung Durchschieben ist (für die Anzeige,
    /// das Segment wandert entsprechend nach außen). 0, wenn nichts zum Durchschieben markiert ist.
    /// </summary>
    public double PushProgress(double dx, double dy)
    {
        if (Phase != RingPhase.Holding || Options.SubmenuPushRadius <= 0 || Highlighted < 0) return 0;
        if (Current?.Segments[Highlighted]?.Action is not SubmenuAction) return 0;
        var start = Options.SubmenuPushRadius * PushProgressStart;
        return Math.Clamp((RingGeometry.Distance(dx, dy) - start) / (Options.SubmenuPushRadius - start), 0, 1);
    }

    /// <summary>Wie <see cref="SelectableIndexAt"/>, aber das markierte Segment bleibt bis kurz hinter seiner Kante markiert.</summary>
    private int TrackedIndexAt(double dx, double dy)
    {
        var index = SelectableIndexAt(dx, dy);
        if (Highlighted < 0 || index == Highlighted || Current is not { } ring || Highlighted >= ring.Segments.Count) return index;
        if (RingGeometry.Distance(dx, dy) < Options.Deadzone) return index;
        var fromCenter = RingGeometry.AngularDistance(RingGeometry.AngleDegrees(dx, dy),
            RingGeometry.SegmentCenterAngle(Highlighted, ring.Segments.Count));
        return fromCenter <= 180.0 / ring.Segments.Count + HysteresisDegrees ? Highlighted : index;
    }

    /// <summary>Index des auswählbaren Segments unter dem Versatz, -1 bei Deadzone/leerem Platz.</summary>
    public int SelectableIndexAt(double dx, double dy)
    {
        var ring = Current;
        if (ring is null) return -1;
        var index = RingGeometry.SegmentAt(dx, dy, ring.Segments.Count, Options.Deadzone);
        if (index < 0) return -1;
        var segment = ring.Segments[index];
        return segment?.Action is null ? -1 : index;
    }

    private RingCommand Activate(int index, long nowMs)
    {
        var ring = Current!;
        var segment = ring.Segments[index]!;
        if (segment.Action is SubmenuAction) return EnterSubmenu(index, nowMs, keepHolding: false);

        var name = CurrentName!;
        Reset();
        return new RingCommand.Execute(segment, name, index);
    }

    /// <param name="keepHolding">true = Taste ist noch gedrückt (Weiterschieben), sonst bleibt das Untermenü zum Klicken offen.</param>
    private RingCommand EnterSubmenu(int index, long nowMs, bool keepHolding)
    {
        if (Current!.Segments[index]!.Action is not SubmenuAction sub) return RingCommand.None;
        var child = _lookup(sub.Ring);
        if (child is null || child.Segments.Count == 0 || _stack.Count >= MaxSubmenuDepth) return RingCommand.None;
        _stack.Push((sub.Ring, child));
        Highlighted = -1;
        Phase = keepHolding ? RingPhase.Holding : RingPhase.Open;
        _leftDeadzone = false;
        _phaseStartedMs = _lastActivityMs = nowMs;
        return new RingCommand.SwitchRing(sub.Ring, child, true);
    }

    private RingCommand CloseWith(RingCloseReason reason)
    {
        Reset();
        return new RingCommand.Close(reason);
    }

    private void Reset()
    {
        _stack.Clear();
        Phase = RingPhase.Closed;
        Highlighted = -1;
        Trigger = 0;
        _leftDeadzone = false;
    }
}
