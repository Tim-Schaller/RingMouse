# RingMouse

Der **Actions Ring** von Logi Options+ als kleine eigenständige App für Windows 11 – dazu die Mausfunktionen, die
dazugehören: **Akku-Anzeige** im Tray mit Warnungen, **DPI**, Belegung weiterer Tasten und **App-Profile** für
Logitech-Mäuse. RingMouse übernimmt nur diese Mausfunktionen; alles andere von Options+ (Tastaturen, Webcams, Flow,
Smart Actions, Beleuchtung, Firmware-Updates …) gehört nicht dazu. Kein Treiber, keine Adminrechte, **keine
Telemetrie, keine Netzwerkzugriffe**.

🇬🇧 [English version](README.md)

- Spricht HID++ 2.0 direkt mit der Maus (Bluetooth LE, USB-Kabel oder Bolt-/Unifying-Empfänger).
- Tasten werden per `REPROG_CONTROLS_V4` (0x1B04) umgeleitet. Die vorhandenen Tasten (CIDs) werden immer vom Gerät
  erfragt, nichts ist hartkodiert.
- Umleitungen werden in folgenden Fällen neu gesetzt: nach Reconnect (0x1D4B), Standby, Entsperren, beim
  Wiederauftauchen des Geräts und wenn der Watchdog (alle 30 s) eine verlorene Umleitung findet.
- Wird Abmelden oder Herunterfahren abgebrochen, startet RingMouse nach etwa 20 s von selbst wieder.
- Die Konfiguration steht in einer JSON-Datei und wird bei Änderungen sofort neu geladen.
- Die Oberfläche gibt es auf Deutsch und Englisch (folgt der Windows-Anzeigesprache, umschaltbar).

**Unterstützte Mäuse:** Logitech-Mäuse mit HID++ 2.0, direkt per Bluetooth, über einen Bolt- oder Unifying-Empfänger
oder per Kabel. Akku über `0x1004`, `0x1001` oder `0x1000`, DPI über `0x2201` oder `0x2202`. Getestet ist bisher die
MX Vertical über Bluetooth; andere Modelle sollten funktionieren – Rückmeldungen sind willkommen (siehe
[CONTRIBUTING.md](CONTRIBUTING.md)).

---

## Inhalt

1. [Installation](#installation)
2. [Options+ ablösen](#options-ablösen)
3. [Bedienung des Rings](#bedienung-des-rings)
4. [Konfiguration](#konfiguration) · [Aktionstypen](#aktionstypen) · [Icons](#icons) · [Profile](#profile) · [Import & Export](#import--export)
5. [Akku & Tray](#akku--tray)
6. [Fenster mit Adminrechten (UIPI / uiAccess)](#fenster-mit-adminrechten-uipi--uiaccess)
7. [ringmouse-probe](#ringmouse-probe)
8. [Troubleshooting](#troubleshooting)
9. [Datenschutz](#datenschutz)
10. [Selbst bauen](#selbst-bauen)
11. [Lizenz](#lizenz)

---

## Installation

1. `RingMouse.exe` aus dem neuesten [Release](https://github.com/Tim-Schaller/RingMouse/releases/latest) herunterladen.
   Es ist eine einzelne, eigenständige Datei – .NET muss nicht installiert sein. Im Release stehen auch die
   SHA-256-Prüfsummen.
2. An einen festen Ort kopieren, z.B. `%LOCALAPPDATA%\Programs\RingMouse\`, und starten. Die Datei ist nicht
   codesigniert, deshalb kann Windows SmartScreen beim ersten Start warnen (*Weitere Informationen* → *Trotzdem ausführen*).
3. Beim ersten Start legt RingMouse `%APPDATA%\RingMouse\config.json` mit einem Standard-Ring an: 8 Plätze und die
   Untermenüs *Medien* und *Text*. Sobald die Maus verbunden ist, fragt die **Ersteinrichtung** nach der Ring-Taste:
   einfach die gewünschte Taste an der Maus drücken (z.B. Daumen-, Gesten- oder DPI-Taste). Ändern geht jederzeit unter
   Einstellungen → *Tasten* → **Taste drücken …**.
4. Tray-Symbol → **Einstellungen** → *Allgemein* → **Mit Windows starten** → Speichern.

Aktualisieren: RingMouse beenden (Tray → *Beenden* oder `RingMouse.exe --exit`), Datei ersetzen, wieder starten.

## Options+ ablösen

Options+ und RingMouse streiten sich um dieselben Tasten. Options+ setzt seine Umleitungen bei jedem App-Wechsel neu und
schaltet die „Analytics“-Meldungen für jeden Klick wieder ein.

1. Zuerst die Options+-Einrichtung übernehmen: Die Ersteinrichtung bietet **Aus Logi Options+ übernehmen …** an (auch
   unter Einstellungen → Allgemein). Das bringt den Actions Ring samt Ordnern, die Tastenbelegungen und die App-Profile
   mit – siehe [Import & Export](#import--export).
1. Options+ beenden: Tray → Beenden, dann im Task-Manager `logioptionsplus_agent`, `logioptionsplus` und
   `LogiPluginService` beenden.
2. RingMouse starten. Im Log (`%APPDATA%\RingMouse\logs\ringmouse-*.log`) sollte eine Zeile wie
   `<Mausname> configured … diverted [0x…] Raw-XY [0x…]` mit der CID deiner Ring-Taste stehen (Logs sind englisch).
3. Klappt alles und hast du Options+ nur für diese Maus genutzt: **Logi Options+** deinstallieren. Den **Logi Plugin
   Service** gleich mit entfernen, er ist der Unterbau des Options+-Actions-Rings. Brauchst du Options+ weiter für andere
   Geräte (z.B. Tastatur oder Webcam), behalte es – lass die Maus aber nur von einem der beiden Programme steuern; beide
   gleichzeitig sind nicht getestet.
4. Hat Options+ Tasten *dauerhaft* umgeleitet, zeigt `ringmouse-probe controls` bei diesen Tasten `PERSIST`.
   Zurücksetzen geht mit `ringmouse-probe reset`. RingMouse räumt fremde Umleitungen auf nicht belegten Tasten beim
   Start ohnehin selbst auf.

> Häufige Ursache, wenn der Options+-Ring nur manchmal aufgeht: App-Profile in Options+ (z.B. für Edge, Teams oder
> Office) belegen die Ring-Taste dort anders, etwa mit „Zeigergeschwindigkeit ändern“. RingMouse-Profile **erben** vom
> Standard und weichen nur dort ab, wo du es explizit einstellst.

## Bedienung des Rings

| Modus (`ring.mode`) | Verhalten |
|---|---|
| `hybrid` (Standard) | **Kurz tippen**: Ring bleibt offen, Klick aufs Segment führt aus. **Halten**: Richtung wählen, Loslassen führt aus. |
| `hold` | Taste halten, Richtung wählen, Loslassen führt aus |
| `tap` | Ring bleibt offen, Klick führt aus |

- Die Auswahl läuft **über den Winkel**, nicht über einen Treffertest. Segment 1 liegt oben, weiter geht es im
  Uhrzeigersinn.
- **Untermenüs beim Halten (Durchschieben):** Taste halten und Richtung Untermenü zeigen – das Segment wird markiert.
  Weiter nach außen schieben (über etwa 85 % des Radius) öffnet das Untermenü, die Taste bleibt gedrückt. Über dem
  gewünschten Eintrag loslassen führt ihn aus. Abschaltbar mit `ring.submenuPush: false`. Loslassen auf dem
  Untermenü-Segment öffnet es wie bisher zum Anklicken.
- **Rückmeldung:** Das markierte Segment blendet weich in die Akzentfarbe und gleitet leicht nach außen, beim
  Durchschieben wandert es weiter hinaus. Steht der Zeiger beim Halten still (Raw-XY), zeigt ein Punkt die Bewegung, und
  der normale Mauszeiger ist so lange ausgeblendet (`ring.hideCursor`; er kommt beim Schließen, im Tippen-Modus und nach
  5 s ohne Bewegung zurück, nach einem Absturz beim nächsten Start). Untermenüs zoomen herein und beim Zurückgehen wieder
  heraus, beim Ausführen leuchtet das Segment kurz auf und der Ring blendet aus. An Segmentgrenzen gibt es 5° Hysterese,
  damit die Markierung nicht flackert. `ring.animation: false` schaltet die Animationen ab.
- **Mitte (Deadzone):** Loslassen oder Klicken dort bricht ab. Im Untermenü heißt die Mitte „Zurück“.
- Weitere Wege zum Abbrechen: **Esc**, **Rechtsklick**, Klick außerhalb des Rings, **Taste erneut drücken** (im
  Tippen-Modus). **Rücktaste** führt eine Ebene zurück.
- Kann die Taste **Raw-XY** (z.B. die DPI-Taste der MX Vertical; `ringmouse-probe controls` zeigt es je Taste), bleibt
  der Mauszeiger beim Halten stehen und die Richtung kommt direkt vom Sensor. Sonst wählt der Ring über die
  Zeigerbewegung.
- Am Bildschirmrand wird der Ring in den sichtbaren Bereich geschoben. Danach springt der Zeiger an seine
  Ausgangsposition zurück (`ring.restoreCursor`).
- Das Overlay aktiviert sich nie, der Fokus bleibt im Zielfenster. Die Aktion landet also dort, wo du gerade warst.
- Hell/Dunkel folgt dem Windows-Modus, die Akzentfarbe ebenfalls. Der Ring ist Per-Monitor-DPI-korrekt.
- **Aussehen anpassen** (Einstellungen → Ringe → „Aussehen des Rings“, mit Live-Vorschau):
  - Größe (Radius). „Am Bildschirm ansehen“ zeigt den Ring kurz in echter Größe am Mauszeiger, denn die Vorschau im
    Fenster wird immer eingepasst.
  - Symbole & Schrift in %. Zu lange Wörter werden automatisch kleiner, statt abgeschnitten zu werden.
  - Farbschema, Ringfarbe, Markierungsfarbe und Deckkraft. Schrift und Abstufungen werden aus der Ringfarbe abgeleitet.
  - Zeigerpunkt: Größe und Farbe.
  - Farben gibt es als Farbfelder oder über „Eigene…“ (Windows-Farbdialog). In der Config stehen sie als `"#RRGGBB"`,
    leer bedeutet automatisch.
- **Latenz:** Im Log steht bei jedem Öffnen, wie viele Millisekunden nach dem Tastendruck der Ring sichtbar war.
  Gemessen: rund 24 ms vom HID-Event bis zum ersten Frame.

## Konfiguration

**Datei:** `%APPDATA%\RingMouse\config.json`. Daneben liegt `config.schema.json`, damit Editoren wie VS Code beim Tippen
Vorschläge machen und prüfen (die Beschreibungen im Schema sind englisch).
- Kommentare (`//`) und abschließende Kommas sind erlaubt.
- Änderungen werden **sofort** übernommen.
- Bei einem Fehler bleibt die letzte gültige Config aktiv. Eine Tray-Meldung nennt die Zeile.
- Das Einstellungsfenster schreibt dieselbe Datei. Beim Speichern aus dem Fenster gehen Kommentare verloren.

```jsonc
{
  "$schema": "./config.schema.json",
  "general": {
    "language": "auto",                 // auto | english | german
    "autostart": "run",                 // off | run | task
    "warnIfOptionsPlusRunning": true,
    "disableAnalyticsReporting": true,  // Options+-„Klick-Tracking" der Maus abschalten
    "logLevel": "Information"
  },
  "ring": {
    "mode": "hybrid", "radius": 150, "deadzone": 26, "tapThresholdMs": 350, "submenuPush": true, "hideCursor": true,
    "animation": true, "restoreCursor": true, "useRawXY": "auto", "rawXYScale": 0.6,
    "theme": "system", "showLabels": true, "autoCloseSeconds": 8,
    "ringColor": null, "accentColor": null, "opacity": 100, "textScale": 100,   // null = automatisch, sonst "#RRGGBB"
    "pointerColor": null, "pointerSize": 9
  },
  "buttons": {                          // CID → Aktion (CIDs: Einstellungen → Tasten oder ringmouse-probe controls)
    "0x00FD": { "type": "ring", "ring": "main" }   // legt die Ersteinrichtung für die gedrückte Taste an
  },
  "rings": {
    "main": { "segments": [
      { "label": "Wiedergabe/Pause", "icon": "PlayPause",  "action": { "type": "media", "key": "playPause" } },
      { "label": "Emoji",            "icon": "Emoji",      "action": { "type": "system", "command": "emojiPanel" } },
      { "label": "Medien",           "icon": "Music",      "action": { "type": "submenu", "ring": "media" } },
      { "label": "Sperren",          "icon": "Lock",       "action": { "type": "system", "command": "lock" } },
      { "label": "Text",             "icon": "Edit",       "action": { "type": "submenu", "ring": "text" } },
      { "label": "Bildschirmfoto",   "icon": "Screenshot", "action": { "type": "screenshot" } },
      null,                                              // leerer Platz
      { "label": "Explorer",         "icon": "Folder",     "action": { "type": "launch", "target": "explorer.exe" } }
    ] },
    "text": { "title": "Text", "segments": [
      { "label": "Erledigt", "icon": "Check", "action": { "type": "snippet", "text": "Erledigt {now:dd.MM.yyyy HH:mm}" } },
      { "label": "Grüße",    "icon": "Mail",  "action": { "type": "snippet", "text": "Viele Grüße{newline}{user}" } }
    ] }
  },
  "profiles": [
    { "name": "Excel", "processes": ["excel.exe"],
      "buttons": { "0x0053": { "type": "keys", "keys": "Ctrl+Z" } },   // Zurück = Rückgängig, nur in Excel
      "rings":   { "main": "main-excel" } }                           // eigener Ring in Excel
  ],
  "devices": {
    "*":           { },
    "MX Vertical": { "dpi": 1000 }      // wird nach jedem Reconnect erneut gesetzt
  },
  "battery": { "thresholds": [20, 10, 5], "notifyCharged": true, "pollMinutes": 10, "trayDevice": null },
  "debug": { "rawHidLog": false, "logRingLatency": true }
}
```

### Aktionstypen

| `type` | Felder | Hinweis |
|---|---|---|
| `ring` | `ring` | nur als Tastenbelegung: öffnet einen Ring |
| `submenu` | `ring` | nur im Ring: Untermenü an derselben Stelle |
| `keys` | `keys` | `Ctrl+Shift+S`, `Win+.`, `Alt+F4`, Folge `Ctrl+K, Ctrl+C`. Deutsche Namen erlaubt (Strg, Entf, Pos1 …), Zeichentasten richten sich nach dem aktiven Layout |
| `media` | `key` | `playPause`, `next`, `previous`, `stop`, `volumeUp`, `volumeDown`, `mute` |
| `launch` | `target`, `arguments`, `workingDirectory`, `elevated` | Pfad, Datei, Ordner, URL, URI (`ms-settings:`, `spotify:…`), `shell:AppsFolder\<AUMID>` |
| `snippet` | `text`, `mode` (`type`/`paste`) | Platzhalter `{now:dd.MM.yyyy HH:mm}`, `{date}`, `{time}`, `{clipboard}`, `{user}`, `{computer}`, `{newline}` |
| `powershell` | `script` **oder** `command`, `arguments`, `hidden`, `usePwsh`, `elevated` | Befehle laufen über `-EncodedCommand` (keine Quoting-Probleme) |
| `screenshot` | – | Snipping-Tool-Auswahl (`ms-screenclip:`), Fallback Win+Shift+S |
| `system` | `command` | `lock` (LockWorkStation – Win+L ist per SendInput nicht möglich), `emojiPanel`, `showDesktop`, `taskView`, `clipboardHistory`, `monitorOff`, `openSettings` |
| `dpi` | `values` | ein Wert = setzen, mehrere = durchschalten, z.B. `[1000, 2000]`. Ersetzt die DPI-Funktion einer umgeleiteten DPI-Taste |
| `appKeys` | `process`, `keys`, `restoreFocus`, `launchIfNotRunning` | Hotkey an eine bestimmte App: Fenster kurz aktivieren, senden, Fokus zurück |
| `mouse` | `button` | `left`, `right`, `middle`, `back`, `forward`. Als Tastenbelegung wird die Taste gedrückt gehalten wie die physische |
| `sequence` | `steps` | mehrere Aktionen, dazwischen `{ "type": "delay", "ms": 300 }` |
| `native` | – | Originalfunktion (Taste nicht umleiten) |
| `none` | – | Taste deaktivieren |

**Programme starten ohne Adminrechte:** Läuft RingMouse selbst mit Adminrechten, startet es Programme trotzdem über die
Explorer-Shell ohne Adminrechte. Das gilt nicht, wenn `"elevated": true` gesetzt ist.

**Spotify ohne Web-API:** Options+ steuert Spotify über die Web-API mit Internet und Login. Mit `appKeys` geht es lokal:
Desktop-Hotkeys an das Spotify-Fenster, z.B. `Ctrl+S` Zufall, `Ctrl+R` Wiederholen, `Alt+Shift+B` Gefällt mir. Eine
Playlist öffnet `launch` mit ihrer `spotify:playlist:…`-URI; Abspielen musst du selbst starten.

### Icons

- **Symbolnamen** (Segoe Fluent Icons): `PlayPause Pause Stop Next Previous Volume VolumeUp VolumeDown Mute Emoji Music Album Lock Ticket Tag Screenshot Camera Photo Folder FolderOpen Explorer Shuffle Repeat RepeatOne Heart HeartFill Playlist List Clock Recent Timer Check CheckMark Save Refresh Sync Back Forward Cancel Settings Globe Web Terminal PowerShell Keyboard Mouse Mail Copy Paste Cut Undo Redo Search Calendar Star Link Edit Delete Add Phone People Microphone Video Monitor Desktop TaskView Power Brightness Home Pin Flag Warning Info Code Dpi Speed Headphones Chat Print Share Download Upload Cloud Bluetooth Battery Help Zoom FullScreen Clipboard Laptop Shield Document ChevronRight`
- **Eigene Icons:**
  - `glyph:E72E` für einen beliebigen Codepoint
  - `file:C:\pfad\bild.png` für eine PNG-, ICO- oder JPG-Datei
  - `exe:C:\pfad\app.exe` für das Programmicon
  - `text:AB` für bis zu 3 Zeichen
- **Ohne Angabe:** RingMouse wählt ein Icon passend zur Aktion. Bei `launch` ist das automatisch das Programmicon.

### Profile

- Das **erste** passende Profil gewinnt. Maßgeblich ist der Prozessname des Vordergrundfensters, z.B. `excel.exe`;
  Platzhalter `*` und `?` sind erlaubt.
- Ein Profil überschreibt nur, was darin steht: einzelne `buttons` und/oder `rings` (Ringname → anderer Ring).
- Alles andere kommt aus dem Standard.
- Tasten, die nur in einem Profil belegt sind, werden trotzdem überall umgeleitet. In den anderen Apps bildet RingMouse
  die Originalfunktion nach (Zurück/Vor/Mitte).

### Import & Export

- **Exportieren:** Einstellungen → Allgemein → *Exportieren …* speichert die Konfiguration als `.json`-Datei, z.B. für
  einen anderen Rechner.
- **Importieren:** *Importieren …* nimmt so eine Datei oder ein aus Logi Options+ exportiertes Actions-Ring-Preset
  (`.lp5`). Eine Vorschau zeigt, was übernommen wird und was nicht; du wählst die Teile: Ringe, Tastenbelegungen,
  App-Profile, Geräte-Einstellungen und – bei RingMouse-Dateien – die allgemeinen Einstellungen. Gewählte Teile ersetzen
  gleichnamige Ringe, Profile und Geräte-Einträge sowie dieselben Tasten; alles andere bleibt. Vorher wird eine Sicherung
  der aktuellen Konfiguration angelegt.
- **Aus Logi Options+:** *Aus Logi Options+ importieren …* (auch in der Ersteinrichtung) liest die lokalen Options+-Daten:
  den Actions Ring samt Ordnern (aus dem Logi Plugin Service, auch App-spezifische Ringe) sowie Tastenbelegungen und
  App-Profile der gewählten Maus (aus der Options+-Einstellungsdatenbank, gelesen aus einer Kopie).
  - Übernommen werden: Tastenkürzel, Textbausteine, Programm-/Datei-/URL-Starts, Medien- und Windows-Funktionen, Makros
    daraus sowie die Aktionen des Spotify-Plugins (als lokale Spotify-Hotkeys, Playlists über ihre `spotify:`-URI).
  - Nicht übertragbar: Aktionen anderer Plugins (z.B. Excel, Teams, Photoshop), Drehregler-Funktionen, Gesten,
    Easy-Switch, macOS-Tastenkürzel und „Zeigergeschwindigkeit ändern“. Der Bericht nennt jede einzeln.
  - Gelesen werden nur Profile und Anwendungen – keine Konto-, Analyse- oder sonstigen Options+-Daten.
- `RingMouse.exe --import-report <Datei>` schreibt, was ein Import aus der installierten Options+ ergäbe, ohne etwas zu
  ändern – praktisch für Fehlerberichte.

## Akku & Tray

- **Tray-Symbol:** der Akkustand als Zahl in einem Ring. Der Ring ist das Erkennungszeichen von RingMouse (zur
  Unterscheidung von anderen Prozentanzeigen, z.B. MagicPods) und zeigt den Füllstand als Bogen ab 12 Uhr.
  - weiß bzw. schwarz passend zur Taskleiste, ab 20 % orange, ab 10 % rot
  - grün beim Laden bzw. am Kabel, Häkchen bei 100 %
  - grau, wenn die Maus nicht erreichbar ist (letzter bekannter Stand)
  - „?“ ohne Wert
- **Tooltip:** z.B. `MX Vertical: 64 % · entlädt · Bluetooth LE`. Schläft die Maus, steht dort der zuletzt bekannte
  Stand mit Uhrzeit.
- **Warnungen** bei 20, 10 und 5 %: jede Schwelle einmal pro Entladezyklus. Sie wird erst wieder aktiv, wenn geladen
  wird oder der Stand um 5 Punkte darüber steigt. Dazu kommt „Aufladen abgeschlossen“. Unter Windows 11 erscheinen die
  Meldungen als normale Benachrichtigungen.
- **Quelle:** `0x1004 UNIFIED_BATTERY`, sonst `0x1001 BATTERY_VOLTAGE` mit eigener, per `battery.voltageCurve`
  einstellbarer Li-Ion-Kurve, sonst `0x1000`. Die MX Vertical meldet über `0x1000` Stufenwerte.
- **Aktualisierung:** über Events der Maus, zusätzlich eine Abfrage alle `pollMinutes`. Der letzte Stand liegt in
  `%APPDATA%\RingMouse\state.json`.

## Fenster mit Adminrechten (UIPI / uiAccess)

Windows lässt einen normalen Prozess keine Eingaben an Fenster mit höheren Rechten senden (UIPI). Low-Level-Hooks sehen
dort auch keine Klicks. RingMouse **erkennt und protokolliert** das und zeigt einmal pro Programm eine Meldung. Der Ring
öffnet sich trotzdem, arbeitet in dem Fall aber nur im Halten-Modus.

| Setup | Lösung |
|---|---|
| Konto ist Administrator (UAC mit „Ja“) | Autostart „**Aufgabe mit höchsten Privilegien**“ in den Einstellungen. Die Aufgabe läuft mit normaler Priorität und ohne Laufzeitlimit. |
| **Standardbenutzer + separates Admin-Konto** | „Höchste Privilegien“ bringt nichts, weil Admin-Fenster unter dem anderen Konto laufen. Lösung ist die **uiAccess-Installation**. |

**uiAccess-Installation:** Die Exe wird signiert und nach `C:\Program Files\RingMouse` gelegt. Sie läuft dann ohne
Adminrechte und darf trotzdem Admin-Fenster bedienen. `RingMouse-uiAccess.zip` aus dem Release nehmen (oder selbst bauen
mit `.\build.ps1 -Target Publish`), dann als Administrator:

```powershell
Start-Process powershell -Verb RunAs -ArgumentList '-ExecutionPolicy Bypass -File "<Pfad>\tools\install-uiaccess.ps1"'
```

Das Skript erzeugt ein lokales Codesignatur-Zertifikat, das nicht exportierbar ist und nur diesem Rechner vertraut wird.
Alternativ übergibst du mit `-Thumbprint` eines aus eurer PKI. Anschließend kopiert und signiert es die App. Danach
RingMouse aus `C:\Program Files\RingMouse` starten und *Mit Windows starten* aktivieren. Unter Einstellungen → Allgemein →
Info steht dann „uiAccess ja“.

## ringmouse-probe

Diagnosewerkzeug für die Konsole (Ausgabe englisch). Es braucht keine Adminrechte und liest nur, außer bei `live` und
`reset`.

```text
ringmouse-probe list                     alle Logitech-HID-Collections: Usage Page/Usage, Report-IDs, HID++, BLE/USB/Receiver
ringmouse-probe info | features | controls | battery | dpi [--set N] | dump
ringmouse-probe live --cid 0x00FD [--rawxy] | --all     Taste umleiten, Events roh + dekodiert (Strg+C stellt wieder her)
ringmouse-probe monitor                  nur mitlesen (zeigt auch fremde Software wie Options+)
ringmouse-probe watch [--cid 0x00FD] [--takeover]       DeviceService wie in der App: Reconnect/Standby/Watchdog live
ringmouse-probe reset [--cid ..]         Umleitungen/Remaps/Analytics auf nativ
Optionen: --device <n|PID>  --index <1-6>  --swid <1-15>  --raw  --record frames.jsonl  --duration <s>  --timeout <ms>
```

Beispiel – Befunde an einer MX Vertical über Bluetooth:
- Verbindung über **BLE**, PID `B020`, HID++ 4.5.
- Die HID++-Collection ist `COL02` mit **Usage Page 0xFF43 / Usage 0x0202**. Unter BLE gibt es nur Long Reports `0x11`,
  nicht `0xFF00`.
- Tasten: `0x0050` und `0x0051` sind nicht umleitbar. `0x0052`, `0x0053`, `0x0056` und `0x00FD` (DPI Switch) sind
  umleitbar, jeweils auch mit Raw-XY. `0x00D7` ist eine virtuelle Taste.
- Akku über `0x1000`, DPI 400–4000 in 100er-Schritten.

## Troubleshooting

| Problem | Ursache / Lösung |
|---|---|
| Ring öffnet nicht | Ist eine Ring-Taste belegt (Einstellungen → Tasten)? Läuft Options+? Siehe Tray-Tooltip bzw. Log: dort nach `diverted [0x…]` mit der CID der Taste suchen. Gegenprobe mit `ringmouse-probe live --cid <CID>` |
| Taste ohne Funktion nach einem Absturz | Die temporäre Umleitung ist noch aktiv. Maus kurz aus- und einschalten oder `ringmouse-probe reset`. Ein Neustart von RingMouse setzt sie ebenfalls neu |
| Nach dem Aufwachen braucht der erste Druck einen Moment | BLE verbindet sich neu. RingMouse konfiguriert die Maus nach `0x1D4B`, nach Standby-Ende (+2/+6/+15 s) und per Watchdog |
| Akku zeigt „?“ oder grau | Die Maus schläft und hat noch keinen Wert geliefert. Den letzten Stand zeigt der Tooltip |
| Aktion kommt im Admin-Fenster nicht an | UIPI, siehe [uiAccess](#fenster-mit-adminrechten-uipi--uiaccess) |
| Textbaustein kommt in RDP/Citrix verstümmelt an | `"mode": "paste"` verwenden |
| App-Hotkeys (`appKeys`) ohne Wirkung | Die App muss mit Fenster laufen. Ist sie nur im Tray minimiert, wird sie kurz geöffnet. Bei Spotify lassen sich Hotkeys nicht global setzen |
| `config.json` kaputt | Die alte Config bleibt aktiv. Die Tray-Meldung nennt die Zeile; der Editor zeigt mit `config.schema.json` den Fehler |
| Autostart „Aufgabe“ bringt nichts | Die höheren Rechte wirken nur für Admin-Konten, siehe oben. Die Aufgabe startet RingMouse aber auch beim **Entsperren**, falls es nicht läuft (von älteren Versionen angelegte Aufgaben einmal in den Einstellungen neu setzen, dafür fragt die UAC einmal nach einem Admin-Konto). Der Run-Eintrag startet nur bei der Anmeldung |
| Ring reagiert nach Standby/Bluetooth-Abbruch nicht mehr | Ein eigener Wächter-Thread erkennt eine hängende Geräteverwaltung nach 45 s, bricht den Schritt ab und startet sie notfalls neu. Im Log steht dann `Device management stuck for … s at "<Schritt>"`, alle 10 min eine Zeile zu fehlenden Logitech-Geräten und alle 30 min eine Statuszeile |
| RingMouse reagiert nicht (Windows meldet „keine Rückmeldung“) | Der UI-Wächter schreibt `UI thread not responding for … (step: …)` ins Log – bitte diese Zeile melden |

## Datenschutz

RingMouse hat keine Telemetrie und baut keine Netzwerkverbindungen auf. Alles bleibt auf deinem Rechner:
- `%APPDATA%\RingMouse\` – `config.json`, `config.schema.json`, `state.json` (letzte Akkustände), `logs\`
- `%LOCALAPPDATA%\RingMouse\startup.log` – ein kleines Startprotokoll

Logs und Mitschnitte von `ringmouse-probe dump` enthalten technische Gerätedaten wie die Unit-ID deiner Maus. Prüfe sie,
bevor du sie in einem Fehlerbericht teilst.

## Selbst bauen

```powershell
.\build.ps1                       # Build + Tests + Publish (braucht das .NET 10 SDK)
# Ergebnis:
#   publish\RingMouse\RingMouse.exe          – die App (eine Datei, self-contained)
#   publish\probe\ringmouse-probe.exe        – Diagnosewerkzeug
#   publish\RingMouse-uiAccess\              – Variante für Admin-Fenster (siehe oben)
```

Aufbau, Kommandozeilen-Optionen (`--exit`, `--render-ui`, `--selftest`) und Abhängigkeiten stehen im
[englischen README](README.md#building-from-source). Eine Prüfliste für manuelle Tests liegt in
[docs/testing.md](docs/testing.md).

## Lizenz

[MIT mit „Commons Clause“](LICENSE): RingMouse darf kostenlos genutzt werden, auch beruflich, und darf verändert und
kostenlos weitergegeben werden. Nicht erlaubt ist, RingMouse zu verkaufen oder kostenpflichtige Produkte oder Dienste
anzubieten, deren Wert im Wesentlichen aus RingMouse stammt. Zum Mitwirken siehe [CONTRIBUTING.md](CONTRIBUTING.md).

Logitech, Logi Options+ und MX Vertical sind Marken von Logitech. RingMouse ist ein unabhängiges Projekt und steht in
keiner Verbindung zu Logitech.
