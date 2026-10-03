using System.Windows;
using System.Windows.Controls;
using RingMouse.Core;
using RingMouse.Core.Config;
using RingMouse.Platform;
using RingMouse.Platform.Windows;

namespace RingMouse.App.Settings;

/// <summary>Autostart, Verhalten, Logs und Diagnose.</summary>
internal sealed class GeneralPage : UserControl
{
    private readonly SettingsContext _ctx;
    private readonly TextBlock _autostartStatus = Form.Hint("");

    public GeneralPage(SettingsContext ctx, Action resetToDefaults)
    {
        _ctx = ctx;
        var g = ctx.Config.General;
        var stack = new StackPanel { Margin = new Thickness(12) };

        // Autostart
        var off = new RadioButton { Content = "Nicht automatisch starten", GroupName = "autostart", IsChecked = g.Autostart == AutostartMode.Off };
        var run = new RadioButton { Content = "Mit Windows starten (normale Rechte, HKCU\\…\\Run)", GroupName = "autostart", IsChecked = g.Autostart == AutostartMode.Run };
        var task = new RadioButton { Content = "Als Aufgabe \"Mit höchsten Privilegien\" bei Anmeldung und beim Entsperren", GroupName = "autostart", IsChecked = g.Autostart == AutostartMode.Task };
        off.Checked += (_, _) => SetAutostart(AutostartMode.Off);
        run.Checked += (_, _) => SetAutostart(AutostartMode.Run);
        task.Checked += (_, _) => SetAutostart(AutostartMode.Task);
        var apply = Form.Button("Autostart jetzt einrichten", ApplyAutostart);
        stack.Children.Add(Form.Group("Autostart", off, run, task, Form.Hint(AutostartAdvice()), Form.Buttons(apply), _autostartStatus));
        UpdateAutostartStatus();

        // Verhalten
        stack.Children.Add(Form.Group("Verhalten",
            Form.Check("Warnen, wenn Logi Options+ läuft", g.WarnIfOptionsPlusRunning, v => { g.WarnIfOptionsPlusRunning = v; ctx.MarkDirty(); }),
            Form.Check("Analytics-Key-Events der Maus abschalten (Options+ meldet damit jeden Klick)", g.DisableAnalyticsReporting,
                v => { g.DisableAnalyticsReporting = v; ctx.MarkDirty(); }),
            Form.Check("Raw-HID++-Log aufzeichnen (logs\\hidpp-*.log, nur zur Fehlersuche)", ctx.Config.Debug.RawHidLog,
                v => { ctx.Config.Debug.RawHidLog = v; ctx.MarkDirty(); }),
            Form.Check("Öffnungslatenz des Rings loggen", ctx.Config.Debug.LogRingLatency, v => { ctx.Config.Debug.LogRingLatency = v; ctx.MarkDirty(); }),
            Form.Row("Log-Level", Form.Choice(
                [("Debug", "Debug (ausführlich)"), ("Information", "Information (Standard)"), ("Warning", "Nur Warnungen"), ("Error", "Nur Fehler")],
                g.LogLevel, v => { g.LogLevel = v; ctx.MarkDirty(); }))));

        // Dateien
        stack.Children.Add(Form.Group("Dateien",
            Form.Hint($"Config: {AppPaths.ConfigFile}\nLogs: {AppPaths.LogDirectory}\nDie JSON-Datei ist die Quelle der Wahrheit und wird bei Änderungen sofort neu geladen. " +
                      "Speichern aus diesem Fenster schreibt die Datei neu (Kommentare gehen dabei verloren)."),
            Form.Buttons(
                Form.Button("Config-Datei öffnen", () => ctx.Host.OpenConfigFile()),
                Form.Button("Ordner öffnen", () => ctx.Host.OpenConfigFolder()),
                Form.Button("Logs öffnen", () => ctx.Host.OpenLogs()),
                Form.Button("Standard wiederherstellen …", resetToDefaults))));

        // Info
        var running = OptionsPlusDetector.RunningProcesses();
        stack.Children.Add(Form.Group("Info",
            Form.Row("Version", new TextBlock { Text = typeof(GeneralPage).Assembly.GetName().Version?.ToString() ?? "?" }),
            Form.Row("Programm", new TextBlock { Text = Environment.ProcessPath ?? "?", TextWrapping = TextWrapping.Wrap }),
            Form.Row("Rechte", new TextBlock
            {
                Text = $"{(ProcessRights.IsElevated ? "mit Adminrechten" : "normale Rechte")} · uiAccess {(ProcessRights.HasUiAccess ? "ja" : "nein")} · " +
                       $"Konto {(ProcessRights.UserIsAdministrator ? "Administrator" : "Standardbenutzer")}",
                TextWrapping = TextWrapping.Wrap,
            }),
            Form.Row("Logi Options+", new TextBlock { Text = running.Count == 0 ? "läuft nicht ✔" : $"läuft ({string.Join(", ", running)}) ⚠", TextWrapping = TextWrapping.Wrap }),
            Form.Row("Netzwerk/Telemetrie", new TextBlock { Text = "keine – RingMouse öffnet keine Netzwerkverbindungen" })));

        Content = Form.Scroll(stack);
    }

    private void SetAutostart(AutostartMode mode)
    {
        _ctx.Config.General.Autostart = mode;
        _ctx.MarkDirty();
        UpdateAutostartStatus();
    }

    private void ApplyAutostart()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        var result = _ctx.Host.Autostart.Apply(_ctx.Config.General.Autostart, exe);
        _autostartStatus.Text = (result.Success ? "✔ " : "⚠ ") + result.Message;
    }

    private void UpdateAutostartStatus()
    {
        var current = _ctx.Host.Autostart.DetectCurrent();
        _autostartStatus.Text = $"Derzeit eingerichtet: {current switch { AutostartMode.Run => "Run-Eintrag", AutostartMode.Task => "Aufgabe", _ => "kein Autostart" }}" +
                                (current == _ctx.Config.General.Autostart ? "" : " – \"Autostart jetzt einrichten\" oder Speichern übernimmt die Auswahl.");
    }

    private static string AutostartAdvice()
    {
        if (ProcessRights.HasUiAccess)
            return "Diese Installation läuft mit uiAccess – Ring und Eingaben funktionieren auch in Admin-Fenstern. \"Mit Windows starten\" genügt.";
        if (!ProcessRights.UserIsAdministrator)
            return "Dein Konto ist ein Standardbenutzer (Adminrechte über ein anderes Konto). \"Höchste Privilegien\" bringt dann nichts: " +
                   "Admin-Fenster laufen unter dem anderen Konto. Damit Ring-Aktionen auch dort ankommen, RingMouse per " +
                   "tools\\install-uiaccess.ps1 signiert nach C:\\Program Files installieren (siehe README).";
        return "\"Höchste Privilegien\" lässt RingMouse mit Adminrechten starten, damit Aktionen auch in Admin-Fenstern ankommen (UIPI). " +
               "Programme werden trotzdem ohne Adminrechte gestartet. Einrichten fragt einmal per UAC nach.";
    }
}
