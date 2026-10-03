using System.ComponentModel;
using System.Text.Json.Serialization;

namespace RingMouse.Core.Config;

/// <summary>Wurzel der Konfiguration (%APPDATA%\RingMouse\config.json). Die Datei ist die Quelle der Wahrheit.</summary>
public sealed class RingMouseConfig
{
    [JsonPropertyName("$schema")]
    [JsonPropertyOrder(-10)]
    public string? Schema { get; set; } = "./config.schema.json";

    [Description("Format-Version der Config.")]
    public int Version { get; set; } = 1;

    public GeneralSettings General { get; set; } = new();

    [Description("Ring-Verhalten und -Aussehen.")]
    public RingSettings Ring { get; set; } = new();

    [Description("Standard-Tastenbelegung: CID (z.B. \"0x00FD\", siehe ringmouse-probe controls) → Aktion.")]
    public Dictionary<string, ActionDefinition> Buttons { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [Description("Benannte Ringe. Ein Ring hat bis zu 8 Segmente (null = leerer Platz). Segment 0 liegt oben, dann im Uhrzeigersinn.")]
    public Dictionary<string, RingDefinition> Rings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [Description("App-spezifische Profile. Das erste passende Profil gewinnt; alles, was es nicht überschreibt, kommt aus dem Standard.")]
    public List<ProfileDefinition> Profiles { get; set; } = [];

    [Description("Geräte-Einstellungen. Schlüssel: \"*\" (alle), Teil des Gerätenamens (z.B. \"MX Vertical\") oder Produkt-ID (\"B020\").")]
    public Dictionary<string, DeviceSettings> Devices { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public BatterySettings Battery { get; set; } = new();

    public DebugSettings Debug { get; set; } = new();
}

public enum AutostartMode
{
    [Description("Kein Autostart")] Off,
    [Description("HKCU\\...\\Run (normale Rechte)")] Run,
    [Description("Aufgabenplanung mit höchsten Privilegien (funktioniert auch in Admin-Fenstern)")] Task,
}

public sealed class GeneralSettings
{
    [Description("off | run | task")]
    public AutostartMode Autostart { get; set; } = AutostartMode.Off;

    [Description("Warnen, wenn Logi Options+ läuft (es überschreibt Tastenumleitungen).")]
    public bool WarnIfOptionsPlusRunning { get; set; } = true;

    [Description("Von Options+ aktivierte Analytics-Key-Events (jeder Klick wird gefunkt) abschalten.")]
    public bool DisableAnalyticsReporting { get; set; } = true;

    [Description("Serilog-Level: Verbose | Debug | Information | Warning | Error")]
    public string LogLevel { get; set; } = "Information";
}

public enum RingMode
{
    [Description("Halten: Taste halten, Richtung wählen, Loslassen führt aus")] Hold,
    [Description("Tippen: Ring bleibt offen, Klick auf Segment führt aus")] Tap,
    [Description("Hybrid: kurz tippen = Tippen, halten + bewegen = Halten")] Hybrid,
}

public enum RawXYUsage
{
    Auto,
    On,
    Off,
}

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

public sealed class RingSettings
{
    [Description("hold | tap | hybrid")]
    public RingMode Mode { get; set; } = RingMode.Hybrid;

    [Description("Außenradius des Rings in DIP (logischen Pixeln).")]
    public double Radius { get; set; } = 150;

    [Description("Deadzone in der Mitte in DIP: Loslassen/Klicken darin = Abbruch.")]
    public double Deadzone { get; set; } = 26;

    [Description("Hybrid: Ein Tippen kürzer als diese Zeit (ms) lässt den Ring offen.")]
    public int TapThresholdMs { get; set; } = 350;

    [Description("Beim Halten über ein Untermenü hinaus nach außen schieben öffnet es; die Taste bleibt gedrückt, Loslassen wählt dort aus.")]
    public bool SubmenuPush { get; set; } = true;

    [Description("Mauszeiger beim Halten ausblenden, solange ein Punkt die Raw-XY-Richtung zeigt (im Tippen-Modus bleibt er sichtbar).")]
    public bool HideCursor { get; set; } = true;

    [Description("Dezente Öffnen-Animation.")]
    public bool Animation { get; set; } = true;

    [Description("Mauszeiger nach dem Schließen an die Ausgangsposition zurücksetzen.")]
    public bool RestoreCursor { get; set; } = true;

    [Description("Raw-XY beim Halten: auto (wenn die Taste es kann) | on | off. Mit Raw-XY bleibt der Zeiger beim Halten stehen.")]
    public RawXYUsage UseRawXY { get; set; } = RawXYUsage.Auto;

    [Description("Umrechnung Raw-XY-Zählschritte → DIP.")]
    public double RawXYScale { get; set; } = 0.6;

    [Description("system | light | dark")]
    public ThemePreference Theme { get; set; } = ThemePreference.System;

    [Description("Grundfarbe des Rings \"#RRGGBB\"; leer = passend zu Hell/Dunkel. Schrift und Abstufungen werden daraus abgeleitet.")]
    public string? RingColor { get; set; }

    [Description("Markierungsfarbe \"#RRGGBB\"; leer = Windows-Akzentfarbe.")]
    public string? AccentColor { get; set; }

    [Description("Deckkraft des Rings in Prozent (30–100).")]
    public int Opacity { get; set; } = 100;

    [Description("Größe von Symbolen und Beschriftungen in Prozent (50–200), zusätzlich zum Radius.")]
    public int TextScale { get; set; } = 100;

    [Description("Farbe des Zeigerpunkts beim Halten \"#RRGGBB\"; leer = automatisch (hell auf der Markierung).")]
    public string? PointerColor { get; set; }

    [Description("Durchmesser des Zeigerpunkts in DIP (2–40).")]
    public double PointerSize { get; set; } = 9;

    [Description("Beschriftungen unter den Icons anzeigen.")]
    public bool ShowLabels { get; set; } = true;

    [Description("Tippen-Modus: Ring nach n Sekunden automatisch schließen (0 = nie).")]
    public int AutoCloseSeconds { get; set; } = 8;
}

public sealed class RingDefinition
{
    [Description("Optionaler Titel (wird in der Mitte angezeigt, solange nichts gewählt ist).")]
    public string? Title { get; set; }

    [Description("2–8 Segmente, null = leerer Platz. Index 0 = oben, dann im Uhrzeigersinn.")]
    public List<RingSegment?> Segments { get; set; } = [];
}

public sealed class RingSegment
{
    public string Label { get; set; } = "";

    [Description("Symbolname (z.B. \"Lock\"), \"glyph:E72E\", \"file:C:\\\\pfad\\\\bild.png\", \"exe:C:\\\\pfad\\\\app.exe\" oder \"text:AB\".")]
    public string? Icon { get; set; }

    public ActionDefinition? Action { get; set; }
}

public sealed class ProfileDefinition
{
    public string Name { get; set; } = "";

    public bool Enabled { get; set; } = true;

    [Description("Prozessnamen des Vordergrundfensters, z.B. \"excel.exe\"; Platzhalter * und ? erlaubt.")]
    public List<string> Processes { get; set; } = [];

    [Description("Überschreibt einzelne Tasten (CID → Aktion).")]
    public Dictionary<string, ActionDefinition> Buttons { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [Description("Ersetzt Ringe: Ringname → anderer Ringname (z.B. \"main\": \"main-excel\").")]
    public Dictionary<string, string> Rings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class DeviceSettings
{
    [Description("Sensor-DPI (wird nach jedem Reconnect erneut gesetzt); null = nicht anfassen.")]
    public int? Dpi { get; set; }

    [Description("false = RingMouse lässt dieses Gerät komplett in Ruhe.")]
    public bool Enabled { get; set; } = true;
}

public sealed class BatterySettings
{
    [Description("Warnschwellen in Prozent – jede meldet sich einmal pro Entladezyklus.")]
    public List<int> Thresholds { get; set; } = [20, 10, 5];

    [Description("Meldung \"Aufladen abgeschlossen\".")]
    public bool NotifyCharged { get; set; } = true;

    [Description("Abfrage-Intervall in Minuten, falls keine Akku-Events kommen.")]
    public int PollMinutes { get; set; } = 10;

    [Description("Gerät für das Tray-Icon (Teil des Namens); null = erstes Gerät mit Akku.")]
    public string? TrayDevice { get; set; }

    [Description("Eigene Spannungskurve für 0x1001: Liste von {\"mv\":…, \"percent\":…}.")]
    public List<VoltagePoint>? VoltageCurve { get; set; }
}

public sealed class VoltagePoint
{
    public int Mv { get; set; }
    public int Percent { get; set; }
}

public sealed class DebugSettings
{
    [Description("Alle HID++-Rohframes in logs\\hidpp-*.log mitschreiben.")]
    public bool RawHidLog { get; set; }

    [Description("Öffnungslatenz des Rings loggen.")]
    public bool LogRingLatency { get; set; } = true;
}
