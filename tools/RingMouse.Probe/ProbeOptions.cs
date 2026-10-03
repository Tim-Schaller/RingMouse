using RingMouse.HidPlusPlus;
using RingMouse.HidPlusPlus.Features;

namespace RingMouse.Probe;

internal sealed class ProbeException(string message) : Exception(message);

internal sealed class ProbeOptions
{
    public string Command { get; private set; } = "help";
    public string? Device { get; private set; }
    public byte? ReceiverIndex { get; private set; }
    public byte SoftwareId { get; private set; } = HidppDevice.DefaultSoftwareId;
    public bool Raw { get; private set; }
    public string? RecordPath { get; private set; }
    public int TimeoutMs { get; private set; } = 2000;
    public List<ushort> ControlIds { get; } = [];
    public bool AllControls { get; private set; }
    public bool RawXY { get; private set; }
    public int? SetDpi { get; private set; }
    public bool NoPing { get; private set; }
    public int? DurationSeconds { get; private set; }
    public bool Takeover { get; private set; }
    public bool Verbose { get; private set; }

    public static ProbeOptions Parse(string[] args)
    {
        var o = new ProbeOptions();
        var i = 0;
        if (args.Length > 0 && !args[0].StartsWith('-'))
        {
            o.Command = args[0].ToLowerInvariant();
            i = 1;
        }

        for (; i < args.Length; i++)
        {
            var a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ProbeException($"Option {a} erwartet einen Wert.");

            switch (a.ToLowerInvariant())
            {
                case "--device" or "-d":
                    o.Device = Next();
                    break;
                case "--index":
                    o.ReceiverIndex = byte.TryParse(Next(), out var idx) && idx is >= 1 and <= 6
                        ? idx
                        : throw new ProbeException("--index erwartet 1–6.");
                    break;
                case "--swid":
                    o.SoftwareId = byte.TryParse(Next(), out var sw) && sw is >= 1 and <= 15
                        ? sw
                        : throw new ProbeException("--swid erwartet 1–15.");
                    break;
                case "--raw":
                    o.Raw = true;
                    break;
                case "--record":
                    o.RecordPath = Next();
                    break;
                case "--timeout":
                    o.TimeoutMs = int.TryParse(Next(), out var t) && t is >= 100 and <= 30000
                        ? t
                        : throw new ProbeException("--timeout erwartet 100–30000 ms.");
                    break;
                case "--cid":
                    foreach (var part in Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (!RingMouse.HidPlusPlus.Features.ControlIds.TryParse(part, out var cid))
                            throw new ProbeException($"Ungültige CID: {part} (Beispiel: 0x00FD)");
                        o.ControlIds.Add(cid);
                    }
                    break;
                case "--all":
                    o.AllControls = true;
                    break;
                case "--rawxy":
                    o.RawXY = true;
                    break;
                case "--set":
                    o.SetDpi = int.TryParse(Next(), out var dpi) && dpi is >= 50 and <= 50000
                        ? dpi
                        : throw new ProbeException("--set erwartet einen DPI-Wert.");
                    break;
                case "--no-ping":
                    o.NoPing = true;
                    break;
                case "--takeover":
                    o.Takeover = true;
                    break;
                case "--verbose" or "-v":
                    o.Verbose = true;
                    break;
                case "--duration":
                    o.DurationSeconds = int.TryParse(Next(), out var sec) && sec is >= 1 and <= 86400
                        ? sec
                        : throw new ProbeException("--duration erwartet Sekunden (1–86400).");
                    break;
                case "-h" or "--help" or "/?":
                    o.Command = "help";
                    break;
                default:
                    throw new ProbeException($"Unbekannte Option: {a}");
            }
        }
        return o;
    }

    public const string Usage = """
        ringmouse-probe – HID++-Discovery für Logitech-Geräte (ohne Treiber, ohne Adminrechte)

        Aufruf: ringmouse-probe <befehl> [optionen]

        Befehle:
          list                     Alle Logitech-HID-Collections (VID 046D): Usage Page/Usage, Report-IDs/-Längen,
                                   HID++-Erkennung, Verbindungsart (BLE/USB/Receiver) und Device-Index
          info                     Protokollversion, Name, Typ, Unit-ID, Seriennummer, Firmware
          features                 Feature-Tabelle (Root 0x0000 + FeatureSet 0x0001) mit Index und Version
          controls                 Tasten über REPROG_CONTROLS_V4 (0x1B04): CID, TID, Flags, Reporting-Status
          battery                  Akkustand (0x1004 → 0x1001 → 0x1000)
          dpi [--set N]            Sensor-DPI lesen und optional setzen (0x2201)
          live --cid 0x00FD[,..]   Taste(n) umleiten und Events roh + dekodiert anzeigen (Strg+C beendet und
               [--rawxy] | --all   stellt den vorherigen Zustand wieder her); --all = alle umleitbaren Tasten
          monitor                  Nur mitlesen: alle HID++-Reports des Geräts anzeigen (auch fremde Antworten)
          reset [--cid ..]         Umleitungen, Remaps und Analytics-Events auf nativ zurücksetzen (Reste von Options+)
          dump                     info + features + controls + battery + dpi in einem Rutsch
          watch [--cid ..]         DeviceService wie in der App: Reconnect/Standby/Watchdog/Akku-Events live.
                [--rawxy] [--takeover] Ohne --cid nur lesend; mit --cid werden diese Tasten umgeleitet.
                                   --takeover: zusätzlich fremde Umleitungen aufheben + Analytics aus (wie die App)

        Optionen:
          --device <n|PID>         Gerät aus 'list' (laufende Nummer) oder Produkt-ID, z.B. B020
          --index <1-6>            Gerät am Receiver (Standard: erstes antwortende)
          --swid <1-15>            eigene Software-ID (Standard 10 = 0xA)
          --timeout <ms>           Antwort-Timeout (Standard 2000)
          --raw                    alle gesendeten/empfangenen Frames ausgeben
          --record <datei.jsonl>   alle Frames aufzeichnen (Fixtures für Unit-Tests)
          --duration <s>           live/monitor/watch nach s Sekunden automatisch beenden
          --verbose                watch: auch Debug-Meldungen

        Tipp: Options+ vorher beenden (Tray → Beenden bzw. Dienste/Prozesse logioptionsplus_agent,
        LogiPluginService), sonst überschreibt es Umleitungen.
        """;
}
