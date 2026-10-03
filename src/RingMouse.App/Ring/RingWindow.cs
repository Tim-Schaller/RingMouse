using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RingMouse.App.Ring;

/// <summary>
/// Overlay-Fenster des Rings: topmost, transparent, NOACTIVATE + TOOLWINDOW + TRANSPARENT (rein visuell,
/// click-through, Fokus bleibt im Zielfenster). Wird einmal erzeugt und nur verschoben/ein-/ausgeblendet.
/// </summary>
internal sealed partial class RingWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private static readonly IEasingFunction Ease = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction Spring = Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.1 });

    private readonly Grid _host = new() { IsHitTestVisible = false };
    private readonly ScaleTransform _hostScale = new(1, 1);
    private RingVisual? _visual;
    private (double X, double Y) _centerPx;
    private int _hideGeneration;

    public RingWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -20000;
        Top = -20000;
        Width = 10;
        Height = 10;
        Title = "RingMouse Ring";
        // Kein Fluent-Fensterstil/Backdrop für das transparente Overlay – der Ring zeichnet alles selbst.
        ThemeMode = ThemeMode.None;
        _host.RenderTransformOrigin = new Point(0.5, 0.5);
        _host.RenderTransform = _hostScale;
        Content = _host;
        SourceInitialized += (_, _) => ApplyExtendedStyles();
        DpiChanged += (_, _) => PositionWindow();
    }

    public IntPtr Handle => new WindowInteropHelper(this).EnsureHandle();

    public RingVisual? Visual => _visual;

    public double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>Zeigt den Ring zentriert um <paramref name="centerPx"/> (physische Pixel), mit leichtem Aufspringen.</summary>
    public void Present(RingVisual visual, (double X, double Y) centerPx, bool animate)
    {
        CancelHide();
        SetVisual(visual);
        _centerPx = centerPx;
        PositionWindow();
        if (!IsVisible) Show();
        if (animate) AnimateIn(visual.Root, fromScale: 0.9, fromOpacity: 0.25, ms: 160, Spring);
    }

    /// <summary>
    /// Inhalt tauschen, Position bleibt. Ins Untermenü (<paramref name="forward"/>) zoomt der alte Ring weg und der neue
    /// herein, zurück umgekehrt – beide überblenden kurz.
    /// </summary>
    public void Swap(RingVisual visual, bool animate, bool forward)
    {
        var old = _visual;
        var sizeChanged = old is null || Math.Abs(old.Size - visual.Size) > 0.1;
        if (!animate || old is null || sizeChanged)
        {
            SetVisual(visual);
            if (sizeChanged) PositionWindow();
            if (animate) AnimateIn(visual.Root, forward ? 0.9 : 1.08, 0.2, 160, Ease);
            return;
        }

        _visual = visual;
        // Reste eines noch laufenden Übergangs sofort entfernen
        foreach (var stale in _host.Children.OfType<UIElement>().Where(e => e != old.Root).ToList()) _host.Children.Remove(stale);
        _host.Children.Add(visual.Root);

        var outgoing = old.Root;
        var duration = TimeSpan.FromMilliseconds(130);
        var scale = new ScaleTransform(1, 1);
        outgoing.RenderTransformOrigin = new Point(0.5, 0.5);
        outgoing.RenderTransform = scale;
        var target = forward ? 1.07 : 0.88;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(target, duration) { EasingFunction = Ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(target, duration) { EasingFunction = Ease });
        var fade = new DoubleAnimation(0, duration) { EasingFunction = Ease };
        fade.Completed += (_, _) => _host.Children.Remove(outgoing);
        outgoing.BeginAnimation(OpacityProperty, fade);

        AnimateIn(visual.Root, forward ? 0.9 : 1.08, 0, 170, forward ? Spring : Ease);
    }

    /// <summary>Ausblenden – mit <paramref name="animate"/> kurz ausblenden und leicht schrumpfen.</summary>
    public void HideRing(bool animate = false)
    {
        if (!IsVisible) return;
        var generation = ++_hideGeneration;
        if (!animate)
        {
            FinishHide();
            return;
        }
        var duration = TimeSpan.FromMilliseconds(120);
        var fade = new DoubleAnimation(0, duration) { EasingFunction = Ease };
        fade.Completed += (_, _) =>
        {
            if (generation == _hideGeneration) FinishHide();
        };
        // Absicherung: pausiert das Rendern (z.B. gesperrter Bildschirm), endet die Animation nicht – dann hart ausblenden
        var fallback = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(250) };
        fallback.Tick += (_, _) =>
        {
            fallback.Stop();
            if (generation == _hideGeneration && IsVisible) FinishHide();
        };
        fallback.Start();
        _hostScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, duration) { EasingFunction = Ease });
        _hostScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, duration) { EasingFunction = Ease });
        _host.BeginAnimation(OpacityProperty, fade);
    }

    private void FinishHide()
    {
        Hide();
        ResetHost();
    }

    /// <summary>Ein laufendes Ausblenden abbrechen (neuer Ring öffnet währenddessen).</summary>
    private void CancelHide()
    {
        _hideGeneration++;
        ResetHost();
    }

    private void ResetHost()
    {
        _host.BeginAnimation(OpacityProperty, null);
        _host.Opacity = 1;
        _hostScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _hostScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _hostScale.ScaleX = _hostScale.ScaleY = 1;
    }

    /// <summary>Einmal unsichtbar anzeigen, damit Handle, Layout und Render-Pfad beim ersten echten Öffnen warm sind.</summary>
    public void Warmup(RingVisual visual)
    {
        SetVisual(visual);
        _centerPx = (-20000, -20000);
        PositionWindow();
        Opacity = 0;
        Show();
        Dispatcher.BeginInvoke(() =>
        {
            Hide();
            Opacity = 1;
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void SetVisual(RingVisual visual)
    {
        _visual = visual;
        _host.Children.Clear();
        _host.Children.Add(visual.Root);
    }

    private void PositionWindow()
    {
        if (_visual is null) return;
        var scale = DpiScale;
        var sizePx = (int)Math.Ceiling(_visual.Size * scale);
        var x = (int)Math.Round(_centerPx.X - sizePx / 2.0);
        var y = (int)Math.Round(_centerPx.Y - sizePx / 2.0);
        SetWindowPos(Handle, HWND_TOPMOST, x, y, sizePx, sizePx, SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    private static void AnimateIn(FrameworkElement root, double fromScale, double fromOpacity, int ms, IEasingFunction scaleEase)
    {
        var duration = TimeSpan.FromMilliseconds(ms);
        var scale = new ScaleTransform(fromScale, fromScale);
        root.RenderTransformOrigin = new Point(0.5, 0.5);
        root.RenderTransform = scale;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(fromScale, 1, duration) { EasingFunction = scaleEase });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(fromScale, 1, duration) { EasingFunction = scaleEase });
        root.BeginAnimation(OpacityProperty, new DoubleAnimation(fromOpacity, 1, duration) { EasingFunction = Ease });
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private void ApplyExtendedStyles()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_TOPMOST;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
