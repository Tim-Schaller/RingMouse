using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using RingMouse.Core.Config;
using RingMouse.Core.Import;
using RingMouse.Device;
using RingMouse.HidPlusPlus.Features;
using RingMouse.Platform.Import;
using static RingMouse.Core.Localization.Lang;

namespace RingMouse.App.Settings;

/// <summary>
/// Ersteinrichtung: "Drück die Taste, die den Actions Ring öffnen soll". Die erkannte Taste wird mit dem Hauptring
/// belegt; "Andere Taste" ersetzt sie wieder. Erscheint, wenn eine Maus verbunden ist und noch keine Taste einen Ring öffnet.
/// </summary>
internal sealed class RingSetupWindow : Window
{
    private readonly ISettingsHost _host;
    private readonly Action<ushort, ushort?> _assign;
    private readonly TextBlock _heading = Form.Heading(L("Which button should open the Actions Ring?", "Welche Taste soll den Actions Ring öffnen?"));
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 440, Margin = new Thickness(0, 6, 0, 10) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 440, FontWeight = FontWeights.SemiBold };
    private readonly Button _retry;
    private readonly Button _close;
    private readonly Button? _optionsPlus;
    private CancellationTokenSource? _cts;
    private ushort? _assigned;

    public RingSetupWindow(ISettingsHost host, Action<ushort, ushort?> assign)
    {
        _host = host;
        _assign = assign;
        Title = L("Set up RingMouse", "RingMouse einrichten");
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        try
        {
            Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/ringmouse.ico"));
        }
        catch
        {
            // Icon optional
        }

        _text.Text = L("Press it on your mouse now. Thumb, gesture or DPI buttons work well. " +
                       "The left and right mouse buttons cannot be diverted.",
                       "Drück sie jetzt an deiner Maus. Gut geeignet sind Daumen-, Gesten- oder DPI-Tasten. " +
                       "Linke und rechte Maustaste lassen sich nicht umleiten.");
        _retry = Form.Button(L("Try again", "Nochmal"), () => _ = CaptureAsync());
        _retry.Visibility = Visibility.Collapsed;
        _close = Form.Button(L("Later", "Später"), Close);

        // Umsteiger von Options+: Ring, Tasten und App-Profile mit einem Klick übernehmen
        if (OptionsPlusFiles.Exist())
            _optionsPlus = Form.Button(L("Import from Logi Options+ …", "Aus Logi Options+ übernehmen …"), ImportFromOptionsPlus);
        var buttons = _optionsPlus is null ? Form.Buttons(_retry, _close) : Form.Buttons(_optionsPlus, _retry, _close);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Margin = new Thickness(0, 16, 0, 0);

        var root = new StackPanel { Margin = new Thickness(24, 18, 24, 18), MinWidth = 380 };
        root.Children.Add(_heading);
        root.Children.Add(_text);
        root.Children.Add(_status);
        var hint = Form.Hint(L("You can change it any time later: tray icon → Settings → Buttons → “Press button …”.",
            "Später jederzeit änderbar: Tray-Symbol → Einstellungen → Tasten → „Taste drücken …“."));
        hint.Margin = new Thickness(0, 12, 0, 0);
        root.Children.Add(hint);
        root.Children.Add(buttons);
        Content = root;

        Loaded += (_, _) => _ = CaptureAsync();
        Closed += (_, _) => _cts?.Cancel();
    }

    private async Task CaptureAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _retry.Visibility = Visibility.Collapsed;
        _status.Text = L("One moment …", "Einen Moment …");

        ButtonEvent? pressed;
        try
        {
            pressed = await _host.CaptureButtonAsync(
                () => Dispatcher.BeginInvoke(() =>
                {
                    if (!cts.IsCancellationRequested) _status.Text = L("Ready – press the button now.", "Bereit – drück jetzt die Taste.");
                }),
                cts.Token);
        }
        catch (InvalidOperationException ex)
        {
            ShowRetry(ex.Message); // Erkennung läuft schon (z.B. im Einstellungsfenster)
            return;
        }
        if (cts.IsCancellationRequested) return; // Fenster geschlossen oder neu gestartet

        if (pressed is not { } e)
        {
            ShowRetry(L("No button detected. Is the mouse awake? Move it once and try again.",
                "Keine Taste erkannt. Ist die Maus wach? Einmal bewegen und nochmal versuchen."));
            return;
        }

        _assign(e.ControlId, _assigned);
        _assigned = e.ControlId;
        _heading.Text = L("Done!", "Fertig!");
        _text.Text = L($"“{ControlName(e.ControlId)}” now opens the Actions Ring: hold the button, move in a direction and " +
                       "release to run the action – or tap briefly and click the segment.",
                       $"„{ControlName(e.ControlId)}“ öffnet jetzt den Actions Ring: Taste halten, in eine Richtung bewegen und " +
                       "loslassen führt aus – oder kurz tippen und das Segment anklicken.");
        _status.Text = "";
        _retry.Content = L("Other button", "Andere Taste");
        _retry.Visibility = Visibility.Visible;
        _close.Content = L("Done", "Fertig");
    }

    private void ImportFromOptionsPlus()
    {
        _cts?.Cancel(); // Tasten-Erkennung währenddessen anhalten
        using (var importer = OptionsPlusFiles.Load())
        {
            if (!importer.HasAnything)
            {
                _text.Text = L("No Logi Options+ settings were found. Press the button that should open the ring.",
                    "Keine Einstellungen von Logi Options+ gefunden. Drück die Taste, die den Ring öffnen soll.");
                _ = CaptureAsync();
                return;
            }

            var window = ImportWindow.ForOptionsPlus(importer, _host.Devices);
            window.Owner = this;
            if (window.ShowDialog() == true)
            {
                try
                {
                    _host.ApplyImport(window.Result.Config, window.SelectedParts);
                    var ringButton = window.SelectedParts.HasFlag(ImportParts.Buttons)
                        ? window.Result.Config.Buttons.FirstOrDefault(b => b.Value is OpenRingAction).Key
                        : null;
                    if (ringButton is not null && ControlIds.TryParse(ringButton, out var cid))
                    {
                        _assigned = cid;
                        _heading.Text = L("Done!", "Fertig!");
                        _text.Text = L($"Imported from Logi Options+. “{ControlName(cid)}” opens the Actions Ring as before.",
                            $"Aus Logi Options+ übernommen. „{ControlName(cid)}“ öffnet den Actions Ring wie bisher.");
                        _status.Text = "";
                        _retry.Content = L("Other button", "Andere Taste");
                        _retry.Visibility = Visibility.Visible;
                        _close.Content = L("Done", "Fertig");
                        _optionsPlus!.Visibility = Visibility.Collapsed;
                        return;
                    }
                    _text.Text = L("Imported. Now press the button that should open the ring.",
                        "Übernommen. Jetzt noch die Taste drücken, die den Ring öffnen soll.");
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    _text.Text = L($"Nothing was imported: {ex.Message}", $"Es wurde nichts übernommen: {ex.Message}");
                }
            }
        }
        _ = CaptureAsync();
    }

    private void ShowRetry(string message)
    {
        _status.Text = message;
        _retry.Content = L("Try again", "Nochmal");
        _retry.Visibility = Visibility.Visible;
    }

    private string ControlName(ushort cid) =>
        _host.Devices.SelectMany(d => d.Controls).FirstOrDefault(c => c.ControlId == cid)?.Name ?? ControlIds.GetName(cid);
}
