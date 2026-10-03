using RingMouse.Core.Config;
using RingMouse.Core.Ring;
using Xunit;

namespace RingMouse.Core.Tests;

public class RingInteractionTests
{
    private const ushort Trigger = TestConfigs.RingButton;
    private readonly RingMouseConfig _config = TestConfigs.Sample();

    private RingInteraction NewInteraction() => new(name => _config.Rings.GetValueOrDefault(name));

    private static RingInteractionOptions Options(RingMode mode, bool allowTap = true, int autoCloseMs = 0, double push = 0) =>
        new(mode, Deadzone: 26, OuterRadius: 150, TapThresholdMs: 350, AllowTapMode: allowTap, AutoCloseMs: autoCloseMs,
            SubmenuPushRadius: push);

    // Richtungen im Test-Ring: 0 oben Play/Pause, 1 Emoji, 2 rechts Apps▸, 3 Sperren, 4 unten Text▸, 5 Screenshot, 6 leer, 7 Explorer
    private static (double, double) Up => (0, -90);
    private static (double, double) Right => (90, 0);
    private static (double, double) Down => (0, 90);
    private static (double, double) Left => (-90, 0);
    private static (double, double) UpLeft => (-64, -64);

    [Fact]
    public void Hold_ReleaseOnSegment_Executes()
    {
        var ring = NewInteraction();
        Assert.IsType<RingCommand.Show>(ring.Open("main", Trigger, 0, Options(RingMode.Hold)));
        Assert.Equal(RingPhase.Holding, ring.Phase);

        var hl = Assert.IsType<RingCommand.Highlight>(ring.PointerMoved(Up.Item1, Up.Item2, 50));
        Assert.Equal(0, hl.Index);

        var exec = Assert.IsType<RingCommand.Execute>(ring.TriggerReleased(Trigger, Up.Item1, Up.Item2, 400));
        Assert.IsType<MediaAction>(exec.Segment.Action);
        Assert.Equal(RingPhase.Closed, ring.Phase);
    }

    [Fact]
    public void Hold_ReleaseInCenter_Cancels()
    {
        var ring = NewInteraction();
        ring.Open("main", Trigger, 0, Options(RingMode.Hold));
        var close = Assert.IsType<RingCommand.Close>(ring.TriggerReleased(Trigger, 3, 2, 100));
        Assert.Equal(RingCloseReason.ReleasedInCenter, close.Reason);
    }

    [Fact]
    public void Hold_ReleaseOnEmptySlot_Cancels()
    {
        var ring = NewInteraction();
        ring.Open("main", Trigger, 0, Options(RingMode.Hold));
        Assert.Equal(-1, ring.SelectableIndexAt(Left.Item1, Left.Item2)); // Slot 6 ist leer
        Assert.IsType<RingCommand.Close>(ring.TriggerReleased(Trigger, Left.Item1, Left.Item2, 600));
    }

    [Fact]
    public void Hybrid_QuickTap_StaysOpen_ThenClickExecutes()
    {
        var ring = NewInteraction();
        ring.Open("main", Trigger, 0, Options(RingMode.Hybrid));
        Assert.Same(RingCommand.None, ring.TriggerReleased(Trigger, 0, 0, 120));
        Assert.Equal(RingPhase.Open, ring.Phase);

        var exec = Assert.IsType<RingCommand.Execute>(ring.Click(UpLeft.Item1, UpLeft.Item2, true, 900));
        Assert.IsType<LaunchAction>(exec.Segment.Action);
        Assert.Equal(7, exec.Index);
    }

    [Fact]
    public void Hybrid_LongHoldInCenter_Cancels()
    {
        var ring = NewInteraction();
        ring.Open("main", Trigger, 0, Options(RingMode.Hybrid));
        Assert.IsType<RingCommand.Close>(ring.TriggerReleased(Trigger, 0, 0, 900));
    }

    [Fact]
    public void Hybrid_MovedOutAndBack_Cancels()
    {
        var ring = NewInteraction();
        ring.Open("main", Trigger, 0, Options(RingMode.Hybrid));
        ring.PointerMoved(Right.Item1, Right.Item2, 50);
        ring.PointerMoved(0, 0, 100);
        Assert.IsType<RingCommand.Close>(ring.TriggerReleased(Trigger, 0, 0, 150));
    }

    [Fact]
    public void Hybrid_HoldAndRelease_OnSegment_Executes()
    {
        var ring = NewInteraction();
        ring.Open("main", Trigger, 0, Options(RingMode.Hybrid));
        var exec = Assert.IsType<RingCommand.Execute>(ring.TriggerReleased(Trigger, 64, 64, 200)); // Segment 3
        Assert.Equal(SystemCommand.Lock, Assert.IsType<SystemAction>(exec.Segment.Action).Command);
    }

    [Fact]
    public void Tap_Mode_OpenImmediately_TriggerAgainCancels()
    {
        var ring = NewInteraction();
        ring.Open("main", Trigger, 0, Options(RingMode.Tap));
        Assert.Equal(RingPhase.Open, ring.Phase);
        Assert.Same(RingCommand.None, ring.TriggerReleased(Trigger, 0, 0, 50));
        var close = Assert.IsType<RingCommand.Close>(ring.TriggerPressed(Trigger, 500));
        Assert.Equal(RingCloseReason.TriggerPressedAgain, close.Reason);
    }

    [Fact]
    public void Tap_ClickOutside_Center_RightClick_Escape()
    {
        var ring = NewInteraction();
        ring.Open("main", Trigger, 0, Options(RingMode.Tap));
        Assert.Equal(RingCloseReason.ClickedOutside, Assert.IsType<RingCommand.Close>(ring.Click(400, 0, true, 10)).Reason);

        ring.Open("main", Trigger, 0, Options(RingMode.Tap));
        Assert.Equal(RingCloseReason.Cancelled, Assert.IsType<RingCommand.Close>(ring.Click(1, 1, true, 10)).Reason);

        ring.Open("main", Trigger, 0, Options(RingMode.Tap));
        Assert.Equal(RingCloseReason.Cancelled, Assert.IsType<RingCommand.Close>(ring.Click(90, 0, false, 10)).Reason);

        ring.Open("main", Trigger, 0, Options(RingMode.Tap));
        Assert.Equal(RingCloseReason.Escape, Assert.IsType<RingCommand.Close>(ring.Escape()).Reason);
    }

    [Fact]
    public void Submenu_OpensInPlace_CenterGoesBack()
    {
        var ring = NewInteraction();
        ring.Open("main", Trigger, 0, Options(RingMode.Hold));
        var sw = Assert.IsType<RingCommand.SwitchRing>(ring.TriggerReleased(Trigger, Down.Item1, Down.Item2, 300));
        Assert.Equal("text", sw.Name);
        Assert.True(sw.IsSubmenu);
        Assert.Equal(RingPhase.Open, ring.Phase);

        var back = Assert.IsType<RingCommand.SwitchRing>(ring.Click(0, 0, true, 400));
        Assert.Equal("main", back.Name);
        Assert.False(back.IsSubmenu);

        ring.Click(Down.Item1, Down.Item2, true, 500); // wieder hinein
        var exec = Assert.IsType<RingCommand.Execute>(ring.Click(Up.Item1, Up.Item2, true, 600)); // Erledigt
        Assert.Equal("text", exec.RingName);
        Assert.StartsWith("Erledigt {now", Assert.IsType<SnippetAction>(exec.Segment.Action).Text);
    }

    [Fact]
    public void Hold_PushThroughSubmenu_ReleaseOnItemExecutes()
    {
        var ring = NewInteraction();
        ring.Open("main", Trigger, 0, Options(RingMode.Hybrid, push: 127.5));

        // Richtung Text: zuerst nur markieren …
        Assert.Equal(4, Assert.IsType<RingCommand.Highlight>(ring.PointerMoved(0, 90, 100)).Index);
        // … weiter nach außen geschoben: Untermenü öffnet sich, die Taste gilt weiter als gehalten
        var sw = Assert.IsType<RingCommand.SwitchRing>(ring.PointerMoved(0, 135, 200));
        Assert.Equal("text", sw.Name);
        Assert.True(sw.IsSubmenu);
        Assert.Equal(RingPhase.Holding, ring.Phase);
        Assert.Equal(-1, ring.Highlighted);

        // im Untermenü (Zeiger startet wieder in der Mitte) auf "Erledigt" (oben) und loslassen
        Assert.Equal(0, Assert.IsType<RingCommand.Highlight>(ring.PointerMoved(0, -90, 400)).Index);
        var exec = Assert.IsType<RingCommand.Execute>(ring.TriggerReleased(Trigger, 0, -90, 600));
        Assert.Equal("text", exec.RingName);
        Assert.StartsWith("Erledigt {now", Assert.IsType<SnippetAction>(exec.Segment.Action).Text);
    }

    [Fact]
    public void Hold_PushThrough_OnlyForSubmenus_AndOnlyWhileHolding()
    {
        var ring = NewInteraction();
        ring.Open("main", Trigger, 0, Options(RingMode.Hybrid, push: 127.5));
        // normales Segment weit außen: nur markieren, nichts öffnen
        Assert.Equal(0, Assert.IsType<RingCommand.Highlight>(ring.PointerMoved(0, -140, 100)).Index);
        Assert.Same(RingCommand.None, ring.PointerMoved(0, -145, 150));

        // Tippen-Modus (Ring offen, Taste nicht gehalten): Zeiger weit über Text öffnet nichts
        ring.Open("main", Trigger, 0, Options(RingMode.Tap, push: 127.5));
        Assert.IsType<RingCommand.Highlight>(ring.PointerMoved(0, 140, 100));
        Assert.Equal("main", ring.CurrentName);

        // ausgeschaltet: Loslassen auf Text öffnet es wie bisher zum Klicken
        ring.Open("main", Trigger, 0, Options(RingMode.Hybrid));
        Assert.IsType<RingCommand.Highlight>(ring.PointerMoved(0, 140, 100));
        Assert.Equal("main", ring.CurrentName);
        Assert.IsType<RingCommand.SwitchRing>(ring.TriggerReleased(Trigger, 0, 140, 500));
        Assert.Equal(RingPhase.Open, ring.Phase);
    }

    [Fact]
    public void Highlight_HasHysteresisAtSegmentBorder()
    {
        var ring = NewInteraction();
        ring.Open("main", Trigger, 0, Options(RingMode.Hold));
        Assert.Equal(0, Assert.IsType<RingCommand.Highlight>(ring.PointerMoved(0, -90, 50)).Index);

        // 3° hinter der Grenze zu Segment 1 (22,5°): bleibt Segment 0, auch beim Loslassen
        var (x, y) = RingGeometry.PointAt(25.5, 90);
        Assert.Same(RingCommand.None, ring.PointerMoved(x, y, 100));
        Assert.Equal(0, ring.Highlighted);

        // deutlich drüber: wechselt
        (x, y) = RingGeometry.PointAt(30, 90);
        Assert.Equal(1, Assert.IsType<RingCommand.Highlight>(ring.PointerMoved(x, y, 150)).Index);
        // und zurück bis knapp hinter die Grenze: bleibt jetzt bei Segment 1
        (x, y) = RingGeometry.PointAt(19.5, 90);
        Assert.Same(RingCommand.None, ring.PointerMoved(x, y, 200));
        var exec = Assert.IsType<RingCommand.Execute>(ring.TriggerReleased(Trigger, x, y, 400));
        Assert.Equal(1, exec.Index);
    }

    [Fact]
    public void PushProgress_GrowsTowardsPushRadius_OnlyOverSubmenus()
    {
        var ring = NewInteraction();
        ring.Open("main", Trigger, 0, Options(RingMode.Hold, push: 127.5));
        ring.PointerMoved(0, 60, 50);                       // Text▸ markiert, noch vor dem Start (76,5)
        Assert.Equal(0, ring.PushProgress(0, 60));
        Assert.InRange(ring.PushProgress(0, 102), 0.49, 0.51);
        Assert.Equal(1, ring.PushProgress(0, 127.5));

        ring.PointerMoved(0, -110, 100);                    // Play/Pause: kein Untermenü
        Assert.Equal(0, ring.PushProgress(0, -110));
    }

    [Fact]
    public void Depth_FollowsSubmenus()
    {
        var ring = NewInteraction();
        ring.Open("main", Trigger, 0, Options(RingMode.Hold, push: 127.5));
        Assert.Equal(1, ring.Depth);
        ring.PointerMoved(0, 90, 50);
        ring.PointerMoved(0, 135, 100);
        Assert.Equal(2, ring.Depth);
        ring.Back();
        Assert.Equal(1, ring.Depth);
    }

    [Fact]
    public void NoTapCapture_QuickTapCloses_SubmenuUsesReHold()
    {
        var ring = NewInteraction();
        var opts = Options(RingMode.Hybrid, allowTap: false);

        ring.Open("main", Trigger, 0, opts);
        Assert.IsType<RingCommand.Close>(ring.TriggerReleased(Trigger, 0, 0, 100));

        ring.Open("main", Trigger, 0, opts);
        Assert.IsType<RingCommand.SwitchRing>(ring.TriggerReleased(Trigger, Right.Item1, Right.Item2, 300)); // Apps▸
        Assert.Equal(RingPhase.Open, ring.Phase);
        Assert.Same(RingCommand.None, ring.TriggerPressed(Trigger, 1000)); // erneut halten statt abbrechen
        Assert.Equal(RingPhase.Holding, ring.Phase);
        var exec = Assert.IsType<RingCommand.Execute>(ring.TriggerReleased(Trigger, Up.Item1, Up.Item2, 1300));
        Assert.Equal("Ctrl+S", Assert.IsType<AppKeysAction>(exec.Segment.Action).Keys);
    }

    [Fact]
    public void AutoClose_AfterInactivity()
    {
        var ring = NewInteraction();
        ring.Open("main", Trigger, 0, Options(RingMode.Tap, autoCloseMs: 5000));
        Assert.Same(RingCommand.None, ring.Tick(4000));
        Assert.Equal(RingCloseReason.Timeout, Assert.IsType<RingCommand.Close>(ring.Tick(5200)).Reason);
    }

    [Fact]
    public void UnknownRing_DoesNothing()
    {
        var ring = NewInteraction();
        Assert.Same(RingCommand.None, ring.Open("gibtsnicht", Trigger, 0, Options(RingMode.Hold)));
        Assert.False(ring.IsOpen);
    }

    [Fact]
    public void OtherButtonRelease_IsIgnored()
    {
        var ring = NewInteraction();
        ring.Open("main", Trigger, 0, Options(RingMode.Hold));
        Assert.Same(RingCommand.None, ring.TriggerReleased(0x0053, Up.Item1, Up.Item2, 200));
        Assert.True(ring.IsOpen);
    }
}
