using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RingMouse.Core.Config;
using RingMouse.Core.Input;
using RingMouse.Platform;
using RingMouse.Platform.Clipboard;
using RingMouse.Platform.Input;
using RingMouse.Platform.Processes;
using RingMouse.Platform.Windows;

namespace RingMouse.Actions;

/// <summary>Zugriff auf die Maus-DPI (implementiert von der App über den DeviceService).</summary>
public interface IDpiController
{
    Task<int?> SetDpiAsync(int dpi);
    Task<int?> CycleDpiAsync(IReadOnlyList<int> values);
}

public sealed record ActionFailure(string Source, string Message);

/// <summary>
/// Führt Aktionen nacheinander auf einem eigenen STA-Thread aus (COM für den Explorer-Start,
/// Zwischenablage, Fensteraktivierung). Der Ring-/Eingabe-Pfad wird dadurch nie blockiert.
/// </summary>
public sealed class ActionExecutor : IDisposable
{
    private const int PasteRestoreDelayMs = 400;

    private readonly ILogger _logger;
    private readonly IDpiController? _dpi;
    private readonly Action? _openSettings;
    private readonly BlockingCollection<(ActionDefinition Action, string Source, ForegroundWindowInfo Target)> _queue = new();
    private readonly Thread _thread;
    private readonly HashSet<string> _uipiNotified = new(StringComparer.OrdinalIgnoreCase); // nur Aktions-Thread
    private ProcessLauncher _launcher = null!;
    private ClipboardService? _clipboard;

    public ActionExecutor(ILogger? logger = null, IDpiController? dpi = null, Action? openSettings = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _dpi = dpi;
        _openSettings = openSettings;
        _thread = new Thread(Run) { IsBackground = true, Name = "RingMouse Aktionen" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Aktion fehlgeschlagen oder von Windows blockiert (z.B. UIPI) – für eine Tray-Meldung.</summary>
    public event Action<ActionFailure>? Failed;

    /// <summary>Selbsttest: Aktionen nur protokollieren, nicht ausführen (z.B. kein versehentliches "Sperren").</summary>
    public bool DryRun { get; set; }

    public void Enqueue(ActionDefinition action, string source, ForegroundWindowInfo? target = null)
    {
        if (DryRun)
        {
            _logger.LogInformation("Aktion {Action} ({Source}) nicht ausgeführt (Trockenlauf)", action.Describe(), source);
            return;
        }
        if (!_queue.IsAddingCompleted) _queue.Add((action, source, target ?? ForegroundWindow.Capture()));
    }

    private void Run()
    {
        Native.PumpInit();
        _launcher = new ProcessLauncher(_logger);
        try
        {
            _clipboard = new ClipboardService();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Zwischenablage nicht verfügbar");
        }

        foreach (var (action, source, target) in _queue.GetConsumingEnumerable())
        {
            var sw = Stopwatch.StartNew();
            try
            {
                Execute(action, source, target);
                _logger.LogInformation("Aktion {Action} ({Source}) → {Target} in {Ms} ms", action.Describe(), source,
                    target.ProcessName ?? "?", sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Aktion {Action} ({Source}) fehlgeschlagen", action.Describe(), source);
                Failed?.Invoke(new ActionFailure(source, $"{action.Describe()}: {ex.Message}"));
            }
        }
        _clipboard?.Dispose();
    }

    private void Execute(ActionDefinition action, string source, ForegroundWindowInfo target)
    {
        if (RequiresInput(action) && target.IsLikelyElevated && !ProcessRights.CanDriveElevatedWindows)
        {
            // Pro Zielprozess nur einmal melden – sonst käme bei jeder Aktion in diesem Fenster eine Tray-Meldung.
            if (_uipiNotified.Add(target.ProcessName ?? "?"))
            {
                _logger.LogWarning("Ziel {Process} läuft mit Adminrechten ({Elevation}) – Windows blockiert Eingaben von RingMouse (UIPI)",
                    target.ProcessName, target.Elevation);
                Failed?.Invoke(new ActionFailure(source,
                    $"{target.ProcessName} läuft mit Adminrechten – Windows blockiert die Eingabe (UIPI). Abhilfe: uiAccess-Installation (README)."));
            }
            else
            {
                _logger.LogDebug("Ziel {Process} läuft mit Adminrechten – Eingabe wird vermutlich blockiert (UIPI)", target.ProcessName);
            }
        }

        switch (action)
        {
            case KeysAction k:
                InputInjector.SendKeyStrokes(KeyChordParser.Parse(k.Keys));
                break;

            case MediaAction m:
                InputInjector.SendMedia(m.Key);
                break;

            case LaunchAction l:
                _launcher.Launch(l.Target, l.Arguments, l.WorkingDirectory, l.Elevated);
                break;

            case SnippetAction s:
                InsertText(SnippetTemplate.Expand(s.Text, () => _clipboard?.GetText()), s.Mode);
                break;

            case PowerShellAction ps:
                RunPowerShell(ps, source);
                break;

            case ScreenshotAction:
                try
                {
                    _launcher.Launch("ms-screenclip:");
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "ms-screenclip: nicht verfügbar – sende Win+Shift+S");
                    InputInjector.SendKeyStrokes(KeyChordParser.Parse("Win+Shift+S"));
                }
                break;

            case SystemAction sys:
                ExecuteSystem(sys.Command);
                break;

            case DpiAction d when _dpi is not null:
                var dpi = (d.Values.Count == 1 ? _dpi.SetDpiAsync(d.Values[0]) : _dpi.CycleDpiAsync(d.Values)).GetAwaiter().GetResult();
                if (dpi is null) Failed?.Invoke(new ActionFailure(source, "Keine Maus mit einstellbarer DPI erreichbar."));
                break;

            case AppKeysAction a:
                SendToApp(a, source, target);
                break;

            case MouseAction mouse:
                InputInjector.ClickMouseButton(mouse.Button);
                break;

            case SequenceAction seq:
                foreach (var step in seq.Steps)
                {
                    if (step is DelayAction delay) Thread.Sleep(Math.Clamp(delay.Ms, 0, 60000));
                    else Execute(step, source, ForegroundWindow.Capture());
                }
                break;

            case DelayAction delayOnly:
                Thread.Sleep(Math.Clamp(delayOnly.Ms, 0, 60000));
                break;

            case NativeAction or NoneAction or OpenRingAction or SubmenuAction:
                break;

            default:
                throw new NotSupportedException($"Aktionstyp {action.TypeName} wird hier nicht unterstützt.");
        }
    }

    private static bool RequiresInput(ActionDefinition action) =>
        action is KeysAction or SnippetAction or MouseAction or AppKeysAction ||
        action is SystemAction { Command: SystemCommand.EmojiPanel } ||
        action is SequenceAction s && s.Steps.Any(RequiresInput);

    private void InsertText(string text, SnippetMode mode)
    {
        if (text.Length == 0) return;
        if (mode == SnippetMode.Type || _clipboard is null)
        {
            InputInjector.SendText(text);
            return;
        }

        var snapshot = _clipboard.Snapshot();
        if (!_clipboard.SetText(text))
        {
            _logger.LogWarning("Zwischenablage belegt – tippe den Text stattdessen");
            InputInjector.SendText(text);
            return;
        }
        var sequence = Native.ClipboardSequence();
        Thread.Sleep(30);
        InputInjector.SendKeyStrokes(KeyChordParser.Parse("Ctrl+V"));
        Thread.Sleep(PasteRestoreDelayMs); // Ziel-App liest die Zwischenablage asynchron
        if (Native.ClipboardSequence() == sequence) _clipboard.Restore(snapshot);
        else _logger.LogDebug("Zwischenablage wurde inzwischen geändert – keine Wiederherstellung");
    }

    private void RunPowerShell(PowerShellAction ps, string source)
    {
        var (file, args) = ProcessLauncher.BuildPowerShell(ps.Script, ps.Command, ps.Arguments, ps.Hidden, ps.UsePwsh);
        if (ps.Elevated)
        {
            _launcher.Launch(file, args, elevated: true, hidden: ps.Hidden);
            return;
        }
        if (!ps.Hidden)
        {
            _launcher.Launch(file, args);
            return;
        }

        var process = _launcher.StartHidden(file, args);
        if (process is null) return;
        _ = Task.Run(async () =>
        {
            using (process)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                try
                {
                    await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                    if (process.ExitCode != 0)
                    {
                        _logger.LogWarning("PowerShell ({Source}) beendet mit Exit-Code {Code}", source, process.ExitCode);
                        Failed?.Invoke(new ActionFailure(source, $"PowerShell beendet mit Exit-Code {process.ExitCode}."));
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("PowerShell ({Source}) läuft seit 10 Minuten – wird nicht weiter überwacht", source);
                }
            }
        });
    }

    private void ExecuteSystem(SystemCommand command)
    {
        switch (command)
        {
            case SystemCommand.Lock:
                if (!SystemCommands.LockWorkstation()) throw new InvalidOperationException("LockWorkStation fehlgeschlagen.");
                break;
            case SystemCommand.EmojiPanel:
                InputInjector.SendKeyStrokes(KeyChordParser.Parse("Win+."));
                break;
            case SystemCommand.ShowDesktop:
                InputInjector.SendKeyStrokes(KeyChordParser.Parse("Win+D"));
                break;
            case SystemCommand.TaskView:
                InputInjector.SendKeyStrokes(KeyChordParser.Parse("Win+Tab"));
                break;
            case SystemCommand.ClipboardHistory:
                InputInjector.SendKeyStrokes(KeyChordParser.Parse("Win+V"));
                break;
            case SystemCommand.MonitorOff:
                SystemCommands.MonitorOff();
                break;
            case SystemCommand.OpenSettings:
                _openSettings?.Invoke();
                break;
        }
    }

    /// <summary>Hotkey an eine bestimmte App: Fenster aktivieren, senden, Ausgangszustand wiederherstellen.</summary>
    private void SendToApp(AppKeysAction a, string source, ForegroundWindowInfo target)
    {
        var strokes = KeyChordParser.Parse(a.Keys);
        var window = WindowActivator.FindMainWindow(a.Process);
        if (window is null && !string.IsNullOrWhiteSpace(a.LaunchIfNotRunning))
        {
            _launcher.Launch(a.LaunchIfNotRunning);
            var sw = Stopwatch.StartNew();
            while (window is null && sw.Elapsed < TimeSpan.FromSeconds(10))
            {
                Thread.Sleep(250);
                window = WindowActivator.FindMainWindow(a.Process);
            }
            Thread.Sleep(800); // App fertig laden lassen
        }
        if (window is null)
        {
            Failed?.Invoke(new ActionFailure(source, $"{a.Process} läuft nicht."));
            return;
        }

        var previous = target.Handle;
        var alreadyForeground = previous == window.Handle;
        if (!alreadyForeground && !WindowActivator.Activate(window.Handle, TimeSpan.FromMilliseconds(800)))
        {
            Failed?.Invoke(new ActionFailure(source, $"Fenster von {a.Process} ließ sich nicht aktivieren."));
            return;
        }

        Thread.Sleep(alreadyForeground ? 0 : 80);
        InputInjector.SendKeyStrokes(strokes);
        Thread.Sleep(150);

        if (!a.RestoreFocus || alreadyForeground) return;
        if (window.Minimized) WindowActivator.Minimize(window.Handle);
        else if (!window.Visible) WindowActivator.Hide(window.Handle);
        if (WindowActivator.IsAlive(previous)) WindowActivator.Activate(previous, TimeSpan.FromMilliseconds(500));
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    private static class Native
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int x;
            public int y;
            public uint priv;
        }

        /// <summary>Legt die Message-Queue des Threads an (nötig für AttachThreadInput).</summary>
        public static void PumpInit() => PeekMessage(out _, IntPtr.Zero, 0, 0, 0);

        public static uint ClipboardSequence() => GetClipboardSequenceNumber();
    }
}
