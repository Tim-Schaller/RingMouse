using RingMouse.HidPlusPlus;
using RingMouse.HidPlusPlus.Discovery;
using RingMouse.HidPlusPlus.Features;
using RingMouse.HidPlusPlus.Transport;
using RingMouse.HidPlusPlus.Transport.Windows;

namespace RingMouse.Probe;

internal static class Commands
{
    // ------------------------------------------------------------------ list

    public static async Task<int> ListAsync(ProbeOptions o, CancellationToken ct)
    {
        var transport = new WinHidTransport();
        var all = transport.Enumerate(HidppDiscovery.LogitechVendorId);
        var endpoints = ProbeSession.SortEndpoints(HidppDiscovery.FindEndpoints(all));

        ConsoleOut.Heading($"Logitech-HID-Collections (VID 046D): {all.Count}, davon {endpoints.Count} Gerät(e) mit HID++");
        if (all.Count == 0)
        {
            ConsoleOut.Warn("Keine Logitech-HID-Geräte gefunden.");
            return 1;
        }

        var number = 1;
        foreach (var ep in endpoints)
        {
            Console.WriteLine();
            ConsoleOut.Line(ConsoleColor.White, $"[{number}] {ep.DisplayName}  ·  {ep.BusText}  ·  VID {ep.VendorId:X4} PID {ep.ProductId:X4}");
            ConsoleOut.Hint($"    Knoten: {ep.Key}");
            foreach (var c in ep.AllCollections) PrintCollection(c);

            if (!o.NoPing) await PrintIdentityAsync(transport, ep, o, ct).ConfigureAwait(false);
            number++;
        }

        var others = all.Where(c => !endpoints.Any(e => e.AllCollections.Contains(c))).ToList();
        if (others.Count > 0)
        {
            ConsoleOut.Heading("Weitere Logitech-Collections ohne HID++");
            foreach (var c in others)
            {
                ConsoleOut.Info($"  {c.Product ?? "?"}  ({c.Bus}, PID {c.ProductId:X4})");
                PrintCollection(c);
            }
        }

        Console.WriteLine();
        ConsoleOut.Hint("Gerät wählen mit --device <Nummer|PID>, z.B.: ringmouse-probe controls --device 1");
        return 0;
    }

    private static void PrintCollection(HidDeviceInfo c)
    {
        var ids = $"IDs in [{Hex(c.InputReportIds)}] out [{Hex(c.OutputReportIds)}]" +
                  (c.FeatureReportIds.Count > 0 ? $" feat [{Hex(c.FeatureReportIds)}]" : "");
        var role = HidppDiscovery.IsHidppLongCollection(c) ? "→ HID++ Long (0x11, 20 Byte)"
            : HidppDiscovery.IsHidppShortCollection(c) ? "→ HID++ Short (0x10, 7 Byte)"
            : DescribeUsage(c);
        var color = HidppDiscovery.IsHidppCollection(c) ? ConsoleColor.Green : ConsoleColor.Gray;
        ConsoleOut.Line(color,
            $"    {(c.CollectionTag.Length > 0 ? c.CollectionTag : "—"),-6} UP 0x{c.UsagePage:X4} U 0x{c.Usage:X4}  " +
            $"In {c.InputReportLength,2} Out {c.OutputReportLength,2} Feat {c.FeatureReportLength,2}  {ids}  {role}");
    }

    private static string DescribeUsage(HidDeviceInfo c) => (c.UsagePage, c.Usage) switch
    {
        (0x0001, 0x0002) => "Maus (vom System exklusiv geöffnet)",
        (0x0001, 0x0006) => "Tastatur (vom System exklusiv geöffnet)",
        (0x0001, 0x0080) => "System Control",
        (0x000C, 0x0001) => "Consumer Control (Medientasten)",
        (>= 0xFF00, _) => "Vendor-spezifisch",
        _ => "",
    };

    private static async Task PrintIdentityAsync(IHidTransport transport, HidppEndpoint ep, ProbeOptions o, CancellationToken ct)
    {
        HidppChannel channel;
        try
        {
            channel = ep.Open(transport);
        }
        catch (Exception ex)
        {
            ConsoleOut.Warn($"    Öffnen fehlgeschlagen: {ex.Message}");
            return;
        }

        using (channel)
        {
            if (o.Raw) channel.FrameTraced += (_, dir, frame, _) =>
                ConsoleOut.Hint($"      {(dir == FrameDirection.Tx ? "TX" : "RX")} {HidppMessage.ToHex(frame)}");

            var id = await HidppDiscovery.IdentifyAsync(channel, ep.Bus, o.SoftwareId, ct).ConfigureAwait(false);
            if (!id.Responds)
            {
                ConsoleOut.Warn("    antwortet nicht auf HID++ (schläft? Maus bewegen und erneut versuchen)");
                return;
            }

            if (!id.IsReceiver)
            {
                using var dev = new HidppDevice(channel, HidppMessage.DirectDeviceIndex, o.SoftwareId);
                var name = await TryAsync(() => DeviceIdentity.GetNameAsync(dev, ct)).ConfigureAwait(false);
                var kind = await TryStructAsync(() => DeviceIdentity.GetKindAsync(dev, ct)).ConfigureAwait(false);
                ConsoleOut.Good($"    HID++ {id.DirectProtocol}, direkt verbunden (Device-Index 0xFF)" +
                                (name is null ? "" : $" · Name \"{name}\"") + (kind is null or DeviceKind.Unknown ? "" : $" · {kind}"));
                return;
            }

            ConsoleOut.Good("    Receiver (HID++ 1.0), gekoppelte Geräte:");
            if (id.Slots.Count == 0) ConsoleOut.Info("      (keine antwortenden Geräte)");
            foreach (var slot in id.Slots)
                ConsoleOut.Info($"      Index {slot.DeviceIndex}: {slot.Name ?? "?"} · " +
                                (slot.Protocol is { } p ? $"HID++ {p}" : "antwortet nicht (schläft?)"));
        }
    }

    // ------------------------------------------------------------------ info

    public static async Task<int> InfoAsync(ProbeSession s, CancellationToken ct)
    {
        var d = s.Device;
        ConsoleOut.Heading("Geräteinformationen");
        ConsoleOut.KeyValue("Gerät", s.Endpoint.DisplayName);
        ConsoleOut.KeyValue("Anbindung", s.Identity.IsReceiver ? $"{s.Endpoint.BusText}-Receiver (PID {s.Endpoint.ProductId:X4})" : s.Endpoint.BusText);
        ConsoleOut.KeyValue("VID:PID", $"{s.Endpoint.VendorId:X4}:{s.Endpoint.ProductId:X4}");
        ConsoleOut.KeyValue("Device-Index", d.DeviceIndex == HidppMessage.DirectDeviceIndex ? "0xFF (direkt)" : $"{d.DeviceIndex} (Receiver-Slot)");
        ConsoleOut.KeyValue("Protokoll", s.Protocol is { } p ? $"HID++ {p}" : "?");
        ConsoleOut.KeyValue("Software-ID", $"0x{d.SoftwareId:X} (eigene Antworten erkennbar)");
        foreach (var c in s.Channel.Collections)
            ConsoleOut.KeyValue("Collection", $"{c.CollectionTag} UP 0x{c.UsagePage:X4} U 0x{c.Usage:X4} In {c.InputReportLength} Out {c.OutputReportLength}");

        var name = await TryAsync(() => DeviceIdentity.GetNameAsync(d, ct)).ConfigureAwait(false);
        var kind = await TryStructAsync(() => DeviceIdentity.GetKindAsync(d, ct)).ConfigureAwait(false);
        ConsoleOut.KeyValue("Name (0x0005)", name);
        ConsoleOut.KeyValue("Typ", kind?.ToString());

        var fw = await TryAsync(() => DeviceIdentity.GetFirmwareInfoAsync(d, ct)).ConfigureAwait(false);
        if (fw is not null)
        {
            ConsoleOut.KeyValue("Unit-ID", fw.UnitIdText);
            ConsoleOut.KeyValue("Modell-IDs", string.Join(", ", fw.ModelIds.Select(m => m.ToString("X4"))));
            ConsoleOut.KeyValue("Transporte", $"0x{fw.Transport:X4} ({TransportText(fw.Transport)})");
            var serial = await TryAsync(() => DeviceIdentity.GetSerialNumberAsync(d, ct)).ConfigureAwait(false);
            ConsoleOut.KeyValue("Seriennummer", serial);
            var entities = await TryAsync(() => DeviceIdentity.GetFirmwareEntitiesAsync(d, ct)).ConfigureAwait(false) ?? [];
            var first = true;
            foreach (var e in entities)
            {
                ConsoleOut.KeyValue(first ? "Firmware" : "", $"[{e.Index}] {e.TypeName,-18} {e.VersionText}{(e.Active ? " (aktiv)" : "")}");
                first = false;
            }
        }

        OptionsPlusCheck.WarnIfRunning();
        return 0;
    }

    private static string TransportText(ushort t)
    {
        var parts = new List<string>();
        if ((t & 0x0001) != 0) parts.Add("BT");
        if ((t & 0x0002) != 0) parts.Add("BLE");
        if ((t & 0x0004) != 0) parts.Add("eQuad/Unifying");
        if ((t & 0x0008) != 0) parts.Add("USB");
        return parts.Count == 0 ? "?" : string.Join(", ", parts);
    }

    // ------------------------------------------------------------------ features

    public static async Task<int> FeaturesAsync(ProbeSession s, CancellationToken ct)
    {
        var features = await s.Device.EnumerateFeaturesAsync(ct).ConfigureAwait(false);
        ConsoleOut.Heading($"Features von {s.Endpoint.DisplayName}: {features.Count} (inkl. Root)");
        var table = new ConsoleTable("Idx", "Feature", "Name", "Ver", "Flags");
        foreach (var f in features)
            table.Add(f.Index, $"0x{f.FeatureId:X4}", f.Name, f.Version, f.FlagsText);
        table.Print();

        var interesting = new[] { FeatureIds.ReprogControlsV4, FeatureIds.UnifiedBattery, FeatureIds.BatteryVoltage,
            FeatureIds.BatteryStatus, FeatureIds.AdjustableDpi, FeatureIds.ExtendedAdjustableDpi, FeatureIds.WirelessDeviceStatus };
        Console.WriteLine();
        foreach (var id in interesting)
        {
            var f = features.FirstOrDefault(x => x.FeatureId == id);
            ConsoleOut.Line(f is null ? ConsoleColor.DarkGray : ConsoleColor.Green,
                $"  {(f is null ? "–" : "✔")} 0x{id:X4} {FeatureIds.GetName(id)}{(f is null ? " (nicht vorhanden)" : $" an Index {f.Index}, v{f.Version}")}");
        }
        return 0;
    }

    // ------------------------------------------------------------------ controls

    public static async Task<int> ControlsAsync(ProbeSession s, CancellationToken ct)
    {
        var rc = await ReprogControlsV4Feature.TryCreateAsync(s.Device, ct).ConfigureAwait(false);
        if (rc is null)
        {
            ConsoleOut.Error("Das Gerät hat kein REPROG_CONTROLS_V4 (0x1B04).");
            return 2;
        }

        var controls = await rc.GetAllControlsAsync(ct).ConfigureAwait(false);
        ConsoleOut.Heading($"Tasten (0x1B04 an Index {rc.FeatureIndex}, v{rc.Feature.Version}): {controls.Count}");
        var table = new ConsoleTable("Idx", "CID", "Name", "TID", "Pos", "Grp", "GMask", "Flags", "Reporting");
        var persistent = new List<ControlInfo>();
        foreach (var c in controls)
        {
            var reporting = "–";
            if (c.IsDivertable || c.Flags.HasFlag(ControlFlags.Reprogrammable))
            {
                var r = await TryAsync(() => rc.GetReportingAsync(c.ControlId, ct)).ConfigureAwait(false);
                reporting = r?.StateText ?? "Fehler";
                if (r is { PersistentlyDiverted: true } or { IsRemapped: true }) persistent.Add(c);
            }
            table.Add(c.Index, ControlIds.Format(c.ControlId), c.Name, $"0x{c.TaskId:X4}", c.Position, c.Group,
                $"0x{c.GroupMask:X2}", c.FlagsText, reporting);
        }
        table.Print();

        Console.WriteLine();
        var divertable = controls.Where(c => c.IsDivertable).ToList();
        ConsoleOut.Info($"  Umleitbar (divertable): {string.Join(", ", divertable.Select(c => $"{ControlIds.Format(c.ControlId)} {c.Name}"))}");
        var rawXY = divertable.Where(c => c.SupportsRawXY).ToList();
        ConsoleOut.Info($"  Mit Raw-XY:             {(rawXY.Count == 0 ? "keine" : string.Join(", ", rawXY.Select(c => ControlIds.Format(c.ControlId))))}");
        if (persistent.Count > 0)
            ConsoleOut.Warn($"Dauerhaft umgeleitet/umgemappt: {string.Join(", ", persistent.Select(c => ControlIds.Format(c.ControlId)))} " +
                            "– vermutlich von Options+. Zurücksetzen mit: ringmouse-probe reset --all");
        var example = divertable.FirstOrDefault(c => ControlIds.GetStandardMouseButton(c.ControlId) == StandardMouseButton.None) ?? divertable.FirstOrDefault();
        if (example is not null)
            ConsoleOut.Hint($"  Live testen: ringmouse-probe live --cid {ControlIds.Format(example.ControlId)}   (Strg+C beendet)");
        return 0;
    }

    // ------------------------------------------------------------------ battery

    public static async Task<int> BatteryAsync(ProbeSession s, CancellationToken ct)
    {
        var battery = await BatteryFeature.DetectAsync(s.Device, ct).ConfigureAwait(false);
        if (battery is null)
        {
            ConsoleOut.Error("Kein Akku-Feature (0x1004/0x1001/0x1000) gefunden.");
            return 2;
        }

        var r = await battery.ReadAsync(ct).ConfigureAwait(false);
        ConsoleOut.Heading("Akku");
        ConsoleOut.KeyValue("Feature", $"0x{battery.Feature.FeatureId:X4} {battery.Feature.Name} (Index {battery.FeatureIndex}, v{battery.Feature.Version})");
        ConsoleOut.KeyValue("Ladestand", r.Percent is { } pct ? $"{pct} %{(r.PercentIsEstimated ? " (aus Spannung geschätzt)" : "")}" : "unbekannt");
        ConsoleOut.KeyValue("Stufe", r.Level.ToString());
        ConsoleOut.KeyValue("Status", DescribeState(r.State));
        ConsoleOut.KeyValue("Ext. Versorgung", r.ExternalPower ? "ja" : "nein");
        if (r.VoltageMillivolts is { } mv) ConsoleOut.KeyValue("Spannung", $"{mv} mV");
        return 0;
    }

    public static string DescribeState(ChargeState state) => state switch
    {
        ChargeState.Discharging => "entlädt",
        ChargeState.Charging => "lädt",
        ChargeState.ChargingSlow => "lädt langsam",
        ChargeState.Full => "voll geladen",
        ChargeState.NotCharging => "Netzteil, lädt nicht",
        ChargeState.Error => "Ladefehler",
        _ => "unbekannt",
    };

    // ------------------------------------------------------------------ dpi

    public static async Task<int> DpiAsync(ProbeSession s, CancellationToken ct)
    {
        var dpi = await DpiFeature.DetectAsync(s.Device, ct).ConfigureAwait(false);
        if (dpi is null)
        {
            ConsoleOut.Error("Das Gerät hat kein DPI-Feature (0x2201 ADJUSTABLE_DPI bzw. 0x2202 EXTENDED_ADJUSTABLE_DPI).");
            return 2;
        }

        var sensors = await dpi.GetSensorCountAsync(ct).ConfigureAwait(false);
        var list = await dpi.GetDpiListAsync(0, ct).ConfigureAwait(false);
        var state = await dpi.GetDpiAsync(0, ct).ConfigureAwait(false);
        ConsoleOut.Heading($"DPI (0x{dpi.FeatureId:X4} an Index {dpi.Feature.Index}, v{dpi.Feature.Version})");
        ConsoleOut.KeyValue("Sensoren", sensors.ToString());
        ConsoleOut.KeyValue("Aktuell", $"{state.CurrentDpi} DPI");
        ConsoleOut.KeyValue("Standard", $"{state.DefaultDpi} DPI");
        ConsoleOut.KeyValue("Unterstützt", CompressList(list));

        if (s.Options.SetDpi is { } wanted)
        {
            var target = DpiFeature.Snap(wanted, list);
            var set = await dpi.SetDpiAsync(target, 0, ct).ConfigureAwait(false);
            var after = await dpi.GetDpiAsync(0, ct).ConfigureAwait(false);
            ConsoleOut.Good($"  DPI gesetzt: angefragt {wanted}, gesetzt {set}, Gerät meldet {after.CurrentDpi}. (Nicht persistent – gilt bis zum Reconnect.)");
        }
        return 0;
    }

    private static string CompressList(IReadOnlyList<int> values)
    {
        if (values.Count == 0) return "–";
        if (values.Count > 3)
        {
            var step = values[1] - values[0];
            var even = step > 0 && values.Zip(values.Skip(1)).All(p => p.Second - p.First == step);
            if (even) return $"{values[0]}–{values[^1]} (Schritt {step}, {values.Count} Werte)";
        }
        return string.Join(", ", values);
    }

    // ------------------------------------------------------------------ live

    public static async Task<int> LiveAsync(ProbeSession s, CancellationToken ct)
    {
        var d = s.Device;
        var features = await d.EnumerateFeaturesAsync(ct).ConfigureAwait(false);
        var rc = await ReprogControlsV4Feature.TryCreateAsync(d, ct).ConfigureAwait(false);
        if (rc is null)
        {
            ConsoleOut.Error("Das Gerät hat kein REPROG_CONTROLS_V4 (0x1B04).");
            return 2;
        }
        var controls = await rc.GetAllControlsAsync(ct).ConfigureAwait(false);

        List<ControlInfo> targets;
        if (s.Options.AllControls)
        {
            // Links/Rechts nie umleiten, sonst ist die Maus während des Tests unbenutzbar.
            targets = controls.Where(c => c.IsDivertable && c.ControlId is not (0x0050 or 0x0051)).ToList();
        }
        else if (s.Options.ControlIds.Count > 0)
        {
            targets = [];
            foreach (var cid in s.Options.ControlIds)
            {
                var c = controls.FirstOrDefault(x => x.ControlId == cid)
                        ?? throw new ProbeException($"{ControlIds.Format(cid)} gibt es auf diesem Gerät nicht – siehe 'controls'.");
                if (!c.IsDivertable) ConsoleOut.Warn($"{ControlIds.Format(cid)} {c.Name} ist laut Gerät nicht umleitbar – Versuch trotzdem.");
                targets.Add(c);
            }
        }
        else
        {
            throw new ProbeException("Bitte --cid <CID> oder --all angeben (siehe 'ringmouse-probe controls').");
        }
        if (targets.Count == 0) throw new ProbeException("Keine umleitbaren Tasten gefunden.");

        OptionsPlusCheck.WarnIfRunning();

        var previous = new Dictionary<ushort, ControlReporting>();
        foreach (var t in targets)
            previous[t.ControlId] = await rc.GetReportingAsync(t.ControlId, ct).ConfigureAwait(false);

        var printer = new EventPrinter(features, controls, d.SoftwareId);
        var wireless = features.FirstOrDefault(f => f.FeatureId == FeatureIds.WirelessDeviceStatus);
        var reapply = new SemaphoreSlim(0);
        d.Notification += (_, message, _) =>
        {
            printer.Print(message);
            if (wireless is not null && WirelessStatusEvent.TryParse(message, wireless.Index, out var ws) && ws.ReconfigurationNeeded)
                reapply.Release();
        };

        async Task ApplyAsync()
        {
            foreach (var t in targets)
            {
                bool? raw = s.Options.RawXY ? t.SupportsRawXY : null;
                if (s.Options.RawXY && !t.SupportsRawXY)
                    ConsoleOut.Warn($"{ControlIds.Format(t.ControlId)} unterstützt kein Raw-XY – nur Tasten-Events.");
                var r = await rc.SetDivertAsync(t.ControlId, true, raw, ct).ConfigureAwait(false);
                ConsoleOut.Good($"  umgeleitet: {ControlIds.Format(t.ControlId)} {t.Name} → {r.StateText}");
            }
        }

        ConsoleOut.Heading($"Live-Modus: {targets.Count} Taste(n) umgeleitet – Taste drücken, Strg+C beendet");
        await ApplyAsync().ConfigureAwait(false);
        using var durationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (s.Options.DurationSeconds is { } liveSeconds) durationCts.CancelAfter(TimeSpan.FromSeconds(liveSeconds));
        ct = durationCts.Token;

        try
        {
            while (true)
            {
                await reapply.WaitAsync(ct).ConfigureAwait(false);
                ConsoleOut.Warn("Gerät meldet Reconnect – setze Umleitung neu …");
                await Task.Delay(300, ct).ConfigureAwait(false);
                try
                {
                    await ApplyAsync().ConfigureAwait(false);
                }
                catch (HidppException ex)
                {
                    ConsoleOut.Error($"Neu setzen fehlgeschlagen: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Strg+C
        }
        finally
        {
            Console.WriteLine();
            foreach (var (cid, before) in previous)
            {
                try
                {
                    var r = await rc.RestoreAsync(before, CancellationToken.None).ConfigureAwait(false);
                    ConsoleOut.Info($"  wiederhergestellt: {ControlIds.Format(cid)} → {r.StateText}");
                }
                catch (HidppException ex)
                {
                    ConsoleOut.Error($"Wiederherstellen von {ControlIds.Format(cid)} fehlgeschlagen: {ex.Message}");
                }
            }
        }
        return 0;
    }

    // ------------------------------------------------------------------ monitor

    public static async Task<int> MonitorAsync(ProbeSession s, CancellationToken ct)
    {
        var features = await s.Device.EnumerateFeaturesAsync(ct).ConfigureAwait(false);
        var rc = await ReprogControlsV4Feature.TryCreateAsync(s.Device, ct).ConfigureAwait(false);
        var controls = rc is null ? [] : await rc.GetAllControlsAsync(ct).ConfigureAwait(false);
        var printer = new EventPrinter(features, controls, s.Device.SoftwareId);
        s.Channel.MessageReceived += (_, message, _) => printer.Print(message);

        ConsoleOut.Heading("Monitor: alle HID++-Reports des Geräts (Strg+C beendet)");
        OptionsPlusCheck.WarnIfRunning();
        try
        {
            var duration = s.Options.DurationSeconds is { } sec ? TimeSpan.FromSeconds(sec) : Timeout.InfiniteTimeSpan;
            await Task.Delay(duration, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Strg+C
        }
        return 0;
    }

    // ------------------------------------------------------------------ reset

    public static async Task<int> ResetAsync(ProbeSession s, CancellationToken ct)
    {
        var rc = await ReprogControlsV4Feature.TryCreateAsync(s.Device, ct).ConfigureAwait(false);
        if (rc is null)
        {
            ConsoleOut.Error("Das Gerät hat kein REPROG_CONTROLS_V4 (0x1B04).");
            return 2;
        }
        var controls = await rc.GetAllControlsAsync(ct).ConfigureAwait(false);
        var targets = s.Options.ControlIds.Count > 0
            ? controls.Where(c => s.Options.ControlIds.Contains(c.ControlId)).ToList()
            : controls.Where(c => c.IsDivertable || c.Flags.HasFlag(ControlFlags.Reprogrammable)).ToList();

        OptionsPlusCheck.WarnIfRunning();
        ConsoleOut.Heading("Umleitungen zurücksetzen");
        var table = new ConsoleTable("CID", "Name", "vorher", "nachher");
        foreach (var c in targets)
        {
            var before = await rc.GetReportingAsync(c.ControlId, ct).ConfigureAwait(false);
            var after = before;
            if (!before.IsDefault || before.AnalyticsKeyEvents)
            {
                if (!before.IsDefault) await rc.ClearAllDiversionAsync(c.ControlId, ct).ConfigureAwait(false);
                if (before.IsRemapped)
                    await rc.SetReportingAsync(c.ControlId, ReportingFlags.None, c.ControlId, ct).ConfigureAwait(false);
                if (before.AnalyticsKeyEvents)
                    await rc.SetAnalyticsReportingAsync(c.ControlId, false, ct).ConfigureAwait(false);
                after = await rc.GetReportingAsync(c.ControlId, ct).ConfigureAwait(false);
            }
            table.Add(ControlIds.Format(c.ControlId), c.Name, before.StateText, after.StateText);
        }
        table.Print();
        return 0;
    }

    // ------------------------------------------------------------------ dump

    public static async Task<int> DumpAsync(ProbeSession s, CancellationToken ct)
    {
        var rc = 0;
        foreach (var section in new Func<ProbeSession, CancellationToken, Task<int>>[] { InfoAsync, FeaturesAsync, ControlsAsync, BatteryAsync, DpiAsync })
        {
            try
            {
                rc = Math.Max(rc, await section(s, ct).ConfigureAwait(false));
            }
            catch (HidppException ex)
            {
                ConsoleOut.Error(ex.Message);
                rc = Math.Max(rc, 3);
            }
        }
        return rc;
    }

    // ------------------------------------------------------------------ Hilfen

    private static string Hex(IReadOnlyList<byte> ids) => string.Join(" ", ids.Select(b => b.ToString("X2")));

    private static async Task<T?> TryStructAsync<T>(Func<Task<T>> action) where T : struct
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (HidppException)
        {
            return null;
        }
    }

    private static async Task<T?> TryAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (HidppException)
        {
            return default;
        }
    }
}
