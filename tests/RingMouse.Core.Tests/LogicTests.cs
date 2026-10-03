using System.Globalization;
using RingMouse.Core.Battery;
using RingMouse.Core.Config;
using RingMouse.Core.Input;
using RingMouse.Core.Profiles;
using Xunit;

namespace RingMouse.Core.Tests;

public class ProfileResolverTests
{
    private static RingMouseConfig ConfigWithProfile()
    {
        var c = TestConfigs.Sample();
        c.Rings["main-excel"] = new RingDefinition { Segments = [new RingSegment { Label = "A", Action = new KeysAction { Keys = "Ctrl+Z" } }, null] };
        c.Profiles.Add(new ProfileDefinition
        {
            Name = "Excel",
            Processes = ["EXCEL.EXE"],
            Buttons = { ["0x0053"] = new KeysAction { Keys = "Ctrl+Z" } },
            Rings = { ["main"] = "main-excel" },
        });
        c.Profiles.Add(new ProfileDefinition { Name = "Teams", Processes = ["*teams*"], Buttons = { ["0x0056"] = new NativeAction() } });
        return c;
    }

    [Fact]
    public void Default_WhenNoProfileMatches()
    {
        var r = new ProfileResolver(ConfigWithProfile()).Resolve("notepad.exe");
        Assert.True(r.IsDefault);
        Assert.IsType<OpenRingAction>(r.ActionFor(0x00FD));
        Assert.Null(r.ActionFor(0x0053));
        Assert.Equal(8, r.Ring("main")!.Segments.Count);
    }

    [Fact]
    public void Profile_InheritsAndOverrides()
    {
        var r = new ProfileResolver(ConfigWithProfile()).Resolve(@"C:\Program Files\Microsoft Office\root\Office16\excel.exe");
        Assert.Equal("Excel", r.Name);
        Assert.IsType<OpenRingAction>(r.ActionFor(0x00FD)); // geerbt – das Options+-Problem darf es hier nicht geben
        Assert.Equal("Ctrl+Z", Assert.IsType<KeysAction>(r.ActionFor(0x0053)).Keys);
        Assert.Equal(2, r.Ring("main")!.Segments.Count);   // ersetzt
        Assert.Equal(4, r.Ring("text")!.Segments.Count);   // geerbt
    }

    [Fact]
    public void Wildcards()
    {
        var resolver = new ProfileResolver(ConfigWithProfile());
        Assert.Equal("Teams", resolver.Resolve("ms-teams.exe").Name);
        Assert.Equal("Teams", resolver.Resolve("Teams").Name);
    }

    [Fact]
    public void ControlsToDivert_IncludesProfileOnlyButtons_ExcludesNative()
    {
        var set = new ProfileResolver(ConfigWithProfile()).ControlsToDivert();
        Assert.Contains((ushort)0x00FD, set);
        Assert.Contains((ushort)0x0053, set);
        Assert.DoesNotContain((ushort)0x0056, set);
        Assert.Equal(new ushort[] { 0x00FD }, new ProfileResolver(ConfigWithProfile()).RingControls().ToArray());
    }
}

public class BatteryAlertTrackerTests
{
    [Fact]
    public void EachThresholdOncePerCycle()
    {
        var t = new BatteryAlertTracker([20, 10, 5]);
        Assert.Null(t.Update(50, false, false));
        Assert.Equal(new BatteryAlert.Low(20, 20), t.Update(20, false, false));
        Assert.Null(t.Update(19, false, false));
        Assert.Null(t.Update(21, false, false)); // Schwankung ohne Hysterese → keine erneute Meldung
        Assert.Null(t.Update(20, false, false));
        Assert.Equal(new BatteryAlert.Low(10, 9), t.Update(9, false, false));
        Assert.Equal(new BatteryAlert.Low(5, 5), t.Update(5, false, false));
        Assert.Null(t.Update(3, false, false));
    }

    [Fact]
    public void JumpAcrossThresholds_ReportsLowestOnly()
    {
        var t = new BatteryAlertTracker([20, 10, 5]);
        Assert.Equal(new BatteryAlert.Low(5, 4), t.Update(4, false, false));
        Assert.Null(t.Update(3, false, false));
    }

    [Fact]
    public void Charging_RearmsAndReportsFullOnce()
    {
        var t = new BatteryAlertTracker([20, 10, 5]);
        t.Update(8, false, false);
        Assert.Null(t.Update(30, true, false));
        Assert.Equal(new BatteryAlert.Charged(100), t.Update(100, true, true));
        Assert.Null(t.Update(100, true, true));
        Assert.Null(t.Update(99, false, false));
        Assert.Equal(new BatteryAlert.Low(20, 20), t.Update(20, false, false)); // neuer Zyklus
    }

    [Fact]
    public void Hysteresis_RearmsWithoutChargingFlag()
    {
        var t = new BatteryAlertTracker([20]);
        Assert.NotNull(t.Update(20, false, false));
        Assert.Null(t.Update(26, false, false));   // ≥ 20+5 → wieder scharf
        Assert.NotNull(t.Update(19, false, false));
    }

    [Fact]
    public void StateIsReusedAfterRestart()
    {
        var first = new BatteryAlertTracker([20, 10]);
        first.Update(15, false, false);
        var restarted = new BatteryAlertTracker([20, 10], first.State);
        Assert.Null(restarted.Update(15, false, false));
    }
}

public class KeyChordParserTests
{
    [Theory]
    [InlineData("Ctrl+Shift+S", 1, "Ctrl+Shift+S")]
    [InlineData("Win+.", 1, "Win+.")]
    [InlineData("Alt+F4", 1, "Alt+F4")]
    [InlineData("Ctrl++", 1, "Ctrl++")]
    [InlineData("Ctrl+,", 1, "Ctrl+,")]
    [InlineData("Ctrl+K, Ctrl+C", 2, "Ctrl+K")]
    [InlineData("Strg+Entf", 1, "Ctrl+Delete")]
    [InlineData("Win", 1, "Win")]
    [InlineData("F13", 1, "F13")]
    [InlineData("Num5", 1, "Num5")]
    [InlineData("alt+shift+b", 1, "Alt+Shift+B")]
    [InlineData("AltGr+q", 1, "Ctrl+Alt+Q")]
    public void Parses(string text, int count, string first)
    {
        var strokes = KeyChordParser.Parse(text);
        Assert.Equal(count, strokes.Count);
        Assert.Equal(first, strokes[0].ToString());
    }

    [Fact]
    public void ExtendedKeysAreMarked()
    {
        Assert.True(KeyChordParser.Parse("Ctrl+Delete")[0].Key.Extended);
        Assert.False(KeyChordParser.Parse("Ctrl+C")[0].Key.Extended);
        Assert.True(KeyChordParser.Parse(".")[0].Key.IsCharacter);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Foo+X")]
    [InlineData("Ctrl+Blubb")]
    public void RejectsInvalid(string text) => Assert.False(KeyChordParser.TryParse(text, out _, out _));
}

public class SnippetTemplateTests
{
    private static readonly DateTime Fixed = new(2026, 9, 28, 9, 5, 0);
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    [Fact]
    public void NowWithCustomFormat() =>
        Assert.Equal("Erledigt 28.09.2026 09:05", SnippetTemplate.Expand("Erledigt {now:dd.MM.yyyy HH:mm}", now: Fixed, culture: De));

    [Fact]
    public void DefaultsAndEscapes()
    {
        Assert.Equal("28.09.2026", SnippetTemplate.Expand("{date}", now: Fixed, culture: De));
        Assert.Equal("09:05", SnippetTemplate.Expand("{time}", now: Fixed, culture: De));
        Assert.Equal("{literal} bleibt", SnippetTemplate.Expand("{{literal}} bleibt", now: Fixed));
        Assert.Equal("{unbekannt}", SnippetTemplate.Expand("{unbekannt}", now: Fixed));
        Assert.Equal("a\nb", SnippetTemplate.Expand("a{newline}b"));
        Assert.Equal("Umlaute ü ß bleiben", SnippetTemplate.Expand("Umlaute ü ß bleiben"));
    }

    [Fact]
    public void Clipboard_IsReadLazilyOnce()
    {
        var reads = 0;
        var text = SnippetTemplate.Expand("Was ist \"{clipboard}\" ({clipboard})?", () => { reads++; return "X"; });
        Assert.Equal("Was ist \"X\" (X)?", text);
        Assert.Equal(1, reads);
        Assert.True(SnippetTemplate.UsesClipboard("{clipboard}"));
    }
}

public class ConfigTests
{
    [Fact]
    public void DefaultConfig_IsValid_AndRoundTrips()
    {
        var config = DefaultConfig.Create();
        Assert.DoesNotContain(ConfigValidator.Validate(config), i => i.Severity == IssueSeverity.Error);
        Assert.Contains(DefaultConfig.MainRing, config.Rings.Keys);

        var json = ConfigSerializer.Serialize(config);
        Assert.Equal(json, ConfigSerializer.Serialize(ConfigSerializer.Deserialize(json)));
    }

    [Fact]
    public void DefaultConfig_PresetsNoButton_TheFirstRunSetupAsksForIt()
    {
        var config = DefaultConfig.Create();
        Assert.Empty(config.Buttons);
        Assert.Empty(new ProfileResolver(config).RingControls());
    }

    [Fact]
    public void Serializer_RoundTrips()
    {
        var config = TestConfigs.Sample();
        var json = ConfigSerializer.Serialize(config);
        Assert.Contains("\"type\": \"ring\"", json);
        Assert.Contains("Grüße", json); // Umlaute unverändert
        Assert.DoesNotContain("typeName", json); // nur der Diskriminator "type" wird geschrieben
        Assert.Equal("ring", config.Buttons["0x00FD"].TypeName);

        var back = ConfigSerializer.Deserialize(json);
        Assert.Equal(8, back.Rings["MAIN"].Segments.Count); // Schlüssel ohne Groß-/Kleinschreibung
        Assert.Null(back.Rings["main"].Segments[6]);
        Assert.Equal(json, ConfigSerializer.Serialize(back));
    }

    [Fact]
    public void Parses_CommentsTrailingCommas_OutOfOrderType()
    {
        const string json = """
            {
              // Kommentar
              "buttons": { "0x00FD": { "ring": "main", "type": "ring" }, },
              "rings": { "main": { "segments": [
                { "label": "Lock", "action": { "command": "lock", "type": "system" } },
                null,
              ] } },
            }
            """;
        var result = ConfigStore.Parse(json);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(SystemCommand.Lock, Assert.IsType<SystemAction>(result.Config!.Rings["main"].Segments[0]!.Action).Command);
    }

    [Fact]
    public void InvalidJson_ReportsLine()
    {
        var result = ConfigStore.Parse("{\n  \"ring\": { \"radius\": }\n}");
        Assert.False(result.Success);
        Assert.Contains("Zeile 2", result.ErrorMessage);
    }

    [Fact]
    public void Validator_FindsBrokenReferencesAndKeys()
    {
        var c = TestConfigs.Sample();
        c.Buttons["0x0053"] = new OpenRingAction { Ring = "fehlt" };
        c.Buttons["kaputt"] = new NoneAction();
        c.Rings["main"].Segments[0] = new RingSegment { Label = "X", Action = new KeysAction { Keys = "Ctrl+Quatsch" } };
        var errors = ConfigValidator.Validate(c).Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Path).ToList();
        Assert.Contains("buttons.0x0053.ring", errors);
        Assert.Contains("buttons.kaputt", errors);
        Assert.Contains("rings.main.segments[0].action.keys", errors);
    }

    [Fact]
    public void Schema_IsGenerated()
    {
        var schema = ConfigSerializer.ExportSchema();
        Assert.Contains("\"$schema\"", schema);
        Assert.Contains("appKeys", schema);
        Assert.Contains("Warnschwellen", schema);
        System.Text.Json.JsonDocument.Parse(schema).Dispose();
    }

    [Fact]
    public void DeviceMatching_SpecificBeforeWildcard()
    {
        var c = TestConfigs.Sample();
        Assert.Equal(1000, DeviceMatching.Resolve(c, "Sample Mouse Pro", 0x1234, "1A2B3C4D").Dpi);
        Assert.Null(DeviceMatching.Resolve(c, "Sample Keyboard", 0x4321, null).Dpi);
        c.Devices["4321"] = new DeviceSettings { Enabled = false };
        Assert.False(DeviceMatching.Resolve(c, "Sample Keyboard", 0x4321, null).Enabled);
    }
}
