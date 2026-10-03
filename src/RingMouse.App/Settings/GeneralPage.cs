using System.Windows;
using System.Windows.Controls;
using RingMouse.Core;
using RingMouse.Core.Config;
using RingMouse.Platform;
using RingMouse.Platform.Import;
using RingMouse.Platform.Windows;
using static RingMouse.Core.Localization.Lang;

namespace RingMouse.App.Settings;

/// <summary>Befehle des Einstellungsfensters, die auf der Seite "Allgemein" ausgelöst werden.</summary>
internal sealed record GeneralPageCommands(Action ResetToDefaults, Action SaveAndRestart, Action Export, Action Import, Action ImportOptionsPlus);

/// <summary>Sprache, Autostart, Verhalten, Import/Export, Dateien und Diagnose.</summary>
internal sealed class GeneralPage : UserControl
{
    private readonly SettingsContext _ctx;
    private readonly TextBlock _autostartStatus = Form.Hint("");

    public GeneralPage(SettingsContext ctx, GeneralPageCommands commands)
    {
        _ctx = ctx;
        var g = ctx.Config.General;
        var stack = new StackPanel { Margin = new Thickness(12) };

        // Sprache – gilt nach einem Neustart überall (Tray-Menü und Fenster werden beim Start aufgebaut)
        stack.Children.Add(Form.Group(L("Language", "Sprache"),
            Form.Row(L("User interface", "Oberfläche"), Form.Choice(
                [(UiLanguage.Auto, L("Automatic (Windows display language)", "Automatisch (Windows-Anzeigesprache)")),
                 (UiLanguage.English, "English"),
                 (UiLanguage.German, "Deutsch")],
                g.Language, v => { g.Language = v; ctx.MarkDirty(); })),
            Form.Hint(L("A new language takes effect after RingMouse restarts.", "Eine neue Sprache gilt nach einem Neustart von RingMouse.")),
            Form.Buttons(Form.Button(L("Save and restart", "Speichern und neu starten"), commands.SaveAndRestart))));

        // Autostart
        var off = new RadioButton { Content = L("Don't start automatically", "Nicht automatisch starten"), GroupName = "autostart", IsChecked = g.Autostart == AutostartMode.Off };
        var run = new RadioButton
        {
            Content = L("Start with Windows (normal rights, HKCU\\…\\Run)", "Mit Windows starten (normale Rechte, HKCU\\…\\Run)"),
            GroupName = "autostart",
            IsChecked = g.Autostart == AutostartMode.Run,
        };
        var task = new RadioButton
        {
            Content = L("As a scheduled task \"with highest privileges\" at sign-in and on unlock",
                "Als Aufgabe \"Mit höchsten Privilegien\" bei Anmeldung und beim Entsperren"),
            GroupName = "autostart",
            IsChecked = g.Autostart == AutostartMode.Task,
        };
        off.Checked += (_, _) => SetAutostart(AutostartMode.Off);
        run.Checked += (_, _) => SetAutostart(AutostartMode.Run);
        task.Checked += (_, _) => SetAutostart(AutostartMode.Task);
        var apply = Form.Button(L("Set up autostart now", "Autostart jetzt einrichten"), ApplyAutostart);
        stack.Children.Add(Form.Group("Autostart", off, run, task, Form.Hint(AutostartAdvice()), Form.Buttons(apply), _autostartStatus));
        UpdateAutostartStatus();

        // Verhalten
        stack.Children.Add(Form.Group(L("Behavior", "Verhalten"),
            Form.Check(L("Warn if Logi Options+ is running", "Warnen, wenn Logi Options+ läuft"), g.WarnIfOptionsPlusRunning,
                v => { g.WarnIfOptionsPlusRunning = v; ctx.MarkDirty(); }),
            Form.Check(L("Turn off the mouse's analytics key events (Options+ uses them to report every click)",
                    "Analytics-Key-Events der Maus abschalten (Options+ meldet damit jeden Klick)"), g.DisableAnalyticsReporting,
                v => { g.DisableAnalyticsReporting = v; ctx.MarkDirty(); }),
            Form.Check(L("Record raw HID++ log (logs\\hidpp-*.log, for troubleshooting only)", "Raw-HID++-Log aufzeichnen (logs\\hidpp-*.log, nur zur Fehlersuche)"),
                ctx.Config.Debug.RawHidLog, v => { ctx.Config.Debug.RawHidLog = v; ctx.MarkDirty(); }),
            Form.Check(L("Log the ring's opening latency", "Öffnungslatenz des Rings loggen"), ctx.Config.Debug.LogRingLatency,
                v => { ctx.Config.Debug.LogRingLatency = v; ctx.MarkDirty(); }),
            Form.Row(L("Log level", "Log-Level"), Form.Choice(
                [("Debug", L("Debug (verbose)", "Debug (ausführlich)")), ("Information", L("Information (default)", "Information (Standard)")),
                 ("Warning", L("Warnings only", "Nur Warnungen")), ("Error", L("Errors only", "Nur Fehler"))],
                g.LogLevel, v => { g.LogLevel = v; ctx.MarkDirty(); }))));

        // Dateien
        stack.Children.Add(Form.Group(L("Files", "Dateien"),
            Form.Hint(L($"Config: {AppPaths.ConfigFile}\nLogs: {AppPaths.LogDirectory}\nThe JSON file is the source of truth and is reloaded immediately when it changes. " +
                        "Saving from this window rewrites the file (comments are lost).",
                        $"Config: {AppPaths.ConfigFile}\nLogs: {AppPaths.LogDirectory}\nDie JSON-Datei ist die Quelle der Wahrheit und wird bei Änderungen sofort neu geladen. " +
                        "Speichern aus diesem Fenster schreibt die Datei neu (Kommentare gehen dabei verloren).")),
            Form.Buttons(
                Form.Button(L("Open config file", "Config-Datei öffnen"), () => ctx.Host.OpenConfigFile()),
                Form.Button(L("Open folder", "Ordner öffnen"), () => ctx.Host.OpenConfigFolder()),
                Form.Button(L("Open logs", "Logs öffnen"), () => ctx.Host.OpenLogs()),
                Form.Button(L("Restore defaults …", "Standard wiederherstellen …"), commands.ResetToDefaults))));

        // Import & Export
        var importButtons = new List<Button>
        {
            Form.Button(L("Export …", "Exportieren …"), commands.Export),
            Form.Button(L("Import …", "Importieren …"), commands.Import),
        };
        if (OptionsPlusFiles.Exist())
            importButtons.Add(Form.Button(L("Import from Logi Options+ …", "Aus Logi Options+ importieren …"), commands.ImportOptionsPlus));
        stack.Children.Add(Form.Group(L("Import & export", "Import & Export"),
            Form.Hint(L("Export saves the configuration as a file, e.g. for another computer. Import accepts such a file or an Actions Ring " +
                        "preset exported from Logi Options+ (.lp5); you choose which parts to take over.",
                "Exportieren speichert die Konfiguration als Datei, z.B. für einen anderen Rechner. Importieren nimmt so eine Datei oder ein " +
                "aus Logi Options+ exportiertes Actions-Ring-Preset (.lp5); welche Teile übernommen werden, wählst du selbst.")),
            Form.Buttons([.. importButtons])));

        // Info
        var running = OptionsPlusDetector.RunningProcesses();
        stack.Children.Add(Form.Group("Info",
            Form.Row("Version", new TextBlock { Text = typeof(GeneralPage).Assembly.GetName().Version?.ToString() ?? "?" }),
            Form.Row(L("Program", "Programm"), new TextBlock { Text = Environment.ProcessPath ?? "?", TextWrapping = TextWrapping.Wrap }),
            Form.Row(L("Rights", "Rechte"), new TextBlock
            {
                Text = L($"{(ProcessRights.IsElevated ? "with admin rights" : "normal rights")} · uiAccess {(ProcessRights.HasUiAccess ? "yes" : "no")} · " +
                         $"account {(ProcessRights.UserIsAdministrator ? "administrator" : "standard user")}",
                         $"{(ProcessRights.IsElevated ? "mit Adminrechten" : "normale Rechte")} · uiAccess {(ProcessRights.HasUiAccess ? "ja" : "nein")} · " +
                         $"Konto {(ProcessRights.UserIsAdministrator ? "Administrator" : "Standardbenutzer")}"),
                TextWrapping = TextWrapping.Wrap,
            }),
            Form.Row("Logi Options+", new TextBlock
            {
                Text = running.Count == 0
                    ? L("not running ✔", "läuft nicht ✔")
                    : L($"running ({string.Join(", ", running)}) ⚠", $"läuft ({string.Join(", ", running)}) ⚠"),
                TextWrapping = TextWrapping.Wrap,
            }),
            Form.Row(L("Network/telemetry", "Netzwerk/Telemetrie"),
                new TextBlock { Text = L("none – RingMouse opens no network connections", "keine – RingMouse öffnet keine Netzwerkverbindungen") })));

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
        var mode = current switch
        {
            AutostartMode.Run => L("Run entry", "Run-Eintrag"),
            AutostartMode.Task => L("scheduled task", "Aufgabe"),
            _ => L("no autostart", "kein Autostart"),
        };
        _autostartStatus.Text = L($"Currently set up: {mode}", $"Derzeit eingerichtet: {mode}") +
                                (current == _ctx.Config.General.Autostart
                                    ? ""
                                    : L(" – \"Set up autostart now\" or saving applies the selection.",
                                        " – \"Autostart jetzt einrichten\" oder Speichern übernimmt die Auswahl."));
    }

    private static string AutostartAdvice()
    {
        if (ProcessRights.HasUiAccess)
            return L("This installation runs with uiAccess – the ring and input also work in admin windows. \"Start with Windows\" is enough.",
                "Diese Installation läuft mit uiAccess – Ring und Eingaben funktionieren auch in Admin-Fenstern. \"Mit Windows starten\" genügt.");
        if (!ProcessRights.UserIsAdministrator)
            return L("Your account is a standard user (admin rights via another account). \"Highest privileges\" doesn't help then: " +
                     "admin windows run under the other account. To make ring actions reach them too, install RingMouse signed to " +
                     "C:\\Program Files with tools\\install-uiaccess.ps1 (see README).",
                     "Dein Konto ist ein Standardbenutzer (Adminrechte über ein anderes Konto). \"Höchste Privilegien\" bringt dann nichts: " +
                     "Admin-Fenster laufen unter dem anderen Konto. Damit Ring-Aktionen auch dort ankommen, RingMouse per " +
                     "tools\\install-uiaccess.ps1 signiert nach C:\\Program Files installieren (siehe README).");
        return L("\"Highest privileges\" starts RingMouse with admin rights so that actions also reach admin windows (UIPI). " +
                 "Programs are still launched without admin rights. Setting it up asks once via UAC.",
                 "\"Höchste Privilegien\" lässt RingMouse mit Adminrechten starten, damit Aktionen auch in Admin-Fenstern ankommen (UIPI). " +
                 "Programme werden trotzdem ohne Adminrechte gestartet. Einrichten fragt einmal per UAC nach.");
    }
}
