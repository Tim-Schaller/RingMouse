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
            string Next() => i + 1 < args.Length ? args[++i] : throw new ProbeException($"Option {a} expects a value.");

            switch (a.ToLowerInvariant())
            {
                case "--device" or "-d":
                    o.Device = Next();
                    break;
                case "--index":
                    o.ReceiverIndex = byte.TryParse(Next(), out var idx) && idx is >= 1 and <= 6
                        ? idx
                        : throw new ProbeException("--index expects 1–6.");
                    break;
                case "--swid":
                    o.SoftwareId = byte.TryParse(Next(), out var sw) && sw is >= 1 and <= 15
                        ? sw
                        : throw new ProbeException("--swid expects 1–15.");
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
                        : throw new ProbeException("--timeout expects 100–30000 ms.");
                    break;
                case "--cid":
                    foreach (var part in Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (!RingMouse.HidPlusPlus.Features.ControlIds.TryParse(part, out var cid))
                            throw new ProbeException($"Invalid CID: {part} (example: 0x00FD)");
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
                        : throw new ProbeException("--set expects a DPI value.");
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
                        : throw new ProbeException("--duration expects seconds (1–86400).");
                    break;
                case "-h" or "--help" or "/?":
                    o.Command = "help";
                    break;
                default:
                    throw new ProbeException($"Unknown option: {a}");
            }
        }
        return o;
    }

    public const string Usage = """
        ringmouse-probe – HID++ discovery for Logitech devices (no driver, no admin rights)

        Usage: ringmouse-probe <command> [options]

        Commands:
          list                     All Logitech HID collections (VID 046D): usage page/usage, report IDs/lengths,
                                   HID++ detection, connection type (BLE/USB/receiver) and device index
          info                     Protocol version, name, type, unit ID, serial number, firmware
          features                 Feature table (Root 0x0000 + FeatureSet 0x0001) with index and version
          controls                 Buttons via REPROG_CONTROLS_V4 (0x1B04): CID, TID, flags, reporting state
          battery                  Battery level (0x1004 → 0x1001 → 0x1000)
          dpi [--set N]            Read sensor DPI and optionally set it (0x2201)
          live --cid 0x00FD[,..]   Divert button(s) and show events raw + decoded (Ctrl+C quits and
               [--rawxy] | --all   restores the previous state); --all = all divertable buttons
          monitor                  Listen only: show all HID++ reports of the device (including foreign responses)
          reset [--cid ..]         Reset diversions, remaps and analytics events to native (leftovers from Options+)
          dump                     info + features + controls + battery + dpi in one go
          watch [--cid ..]         DeviceService as in the app: reconnect/standby/watchdog/battery events live.
                [--rawxy] [--takeover] Without --cid read-only; with --cid these buttons are diverted.
                                   --takeover: also clear foreign diversions + analytics off (like the app)

        Options:
          --device <n|PID>         Device from 'list' (sequential number) or product ID, e.g. B020
          --index <1-6>            Device on the receiver (default: first one that responds)
          --swid <1-15>            Own software ID (default 10 = 0xA)
          --timeout <ms>           Response timeout (default 2000)
          --raw                    Print all sent/received frames
          --record <file.jsonl>    Record all frames (fixtures for unit tests)
          --duration <s>           End live/monitor/watch automatically after s seconds
          --verbose                watch: debug messages too

        Tip: quit Options+ first (tray → Quit, or the services/processes logioptionsplus_agent,
        LogiPluginService), otherwise it overwrites diversions.
        """;
}
