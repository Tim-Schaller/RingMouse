using RingMouse.Core.Config;
using RingMouse.Core.Import;
using Xunit;

namespace RingMouse.Core.Tests;

/// <summary>Import/Export: Zusammenführen nach Teilen, RingMouse-Dateien und Logi Options+ (erfundene Beispieldaten).</summary>
public class ImportTests
{
    // Actions-Ring-Profil im Loupedeck-Format, wie es der Logi Plugin Service bzw. ein .lp5-Export enthält
    private const string RingProfile = """
        {
          "displayName": "Test Profile",
          "deviceType": "Loupedeck72",
          "layout": {
            "layoutModes": [ { "modeName": "System", "workspaces": [ { "pressPages": [ { "controls": [
              { "controlId": 0, "pressAction": "$DefaultWin___MediaPlayPause" },
              { "controlId": 1, "pressAction": "$@Generic___@ProfileAction___AAAA" },
              { "controlId": 2, "pressAction": "$Spotify___ToggleLike" },
              { "controlId": 3, "pressAction": "$@Generic___@ProfileAction___BBBB" },
              { "controlId": 4, "pressAction": "$@Generic___@Macro___CCCC" },
              { "controlId": 5, "pressAction": "$@Generic___@ProfileAction___DDDD" },
              { "controlId": 6, "pressAction": null },
              { "controlId": 7, "pressAction": "$Excel___CreateChart" }
            ] } ] } ] } ],
            "folderPages": [ { "name": "AAAA", "displayName": "Ordner", "controls": [
              { "controlId": 0, "pressAction": "$@Generic___@ProfileAction___EEEE" },
              { "controlId": 1, "pressAction": "$DefaultWin___LockWorkstation" }
            ] } ]
          },
          "profileActions": [
            { "name": "$@Generic___@ProfileAction___AAAA", "displayName": "Snippets", "templateActionName": "$@Generic___@OpenFolder",
              "actionParameters": { "parameters": { "folderName": "AAAA" } } },
            { "name": "$@Generic___@ProfileAction___BBBB", "displayName": "Save as", "templateActionName": "$@Generic___@KeyboardKey",
              "actionParameters": { "parameters": { "keyboardKey": "ControlOrCommand+Shift+KeyS___4108___Ctrl+Shift+S___win-83" } } },
            { "name": "$@Generic___@ProfileAction___DDDD", "displayName": "Mac lock", "templateActionName": "$@Generic___@KeyboardKey",
              "actionParameters": { "parameters": { "keyboardKey": "ControlOrCommand+Control+KeyQ___4108___Cmd+Ctrl+Q___mac-12" } } },
            { "name": "$@Generic___@ProfileAction___EEEE", "displayName": "Greeting", "templateActionName": "$@Generic___@SendText",
              "actionParameters": { "parameters": { "text": "Hello world", "useClipboard": "true" } } }
          ],
          "macroCommands": [
            { "name": "CCCC", "displayName": "Example", "actions": [ "$@Generic___@ExecuteApplication___https://example.org" ] }
          ]
        }
        """;

    // Ausschnitt des Options+-Einstellungsdokuments (settings.db): Standardprofil, ein App-Profil, Anwendungen
    private const string Settings = """
        {
          "accounts_refresh_token": "not-to-be-read",
          "applications": { "applications": [
            { "applicationId": "application_id_excel", "name": "Excel", "applicationPath": "c:\\office\\excel.exe",
              "applicationPathsList": [ "c:\\office\\excel.exe" ] } ] },
          "profile-1111": { "name": "PROFILE_NAME_DEFAULT", "assignments": [
            { "slotId": "mx-anywhere-3s-b037_c195", "card": { "attribute": "MACRO_PLAYBACK", "name": "ASSIGNMENT_NAME_SHOW_RADIAL_MENU",
              "macro": { "type": "SYSTEM", "system": { "action": "SHOW_RADIAL_MENU" } } } },
            { "slotId": "mx-anywhere-3s-b037_c83", "card": { "attribute": "MACRO_PLAYBACK", "name": "ASSIGNMENT_NAME_BACK",
              "macro": { "type": "MOUSE", "mouse": { "action": "WIN_BACK" } } } },
            { "slotId": "mx-anywhere-3s-b037_c86", "card": { "attribute": "MACRO_PLAYBACK", "name": "ASSIGNMENT_NAME_UNDO",
              "macro": { "type": "KEYSTROKE", "actionName": "Ctrl + Z", "keystroke": { "code": 29, "modifiers": [ 224 ] } } } },
            { "slotId": "mx-anywhere-3s-b037_mouse_settings", "card": { "attribute": "MOUSE_SETTINGS" } },
            { "slotId": "k850-6b34d_c10", "card": { "attribute": "MACRO_PLAYBACK",
              "macro": { "type": "KEYSTROKE", "keystroke": { "code": 4, "modifiers": [] } } } }
          ] },
          "profile-application_id_excel": { "applicationId": "application_id_excel", "baseProfileId": "1111", "assignments": [
            { "slotId": "mx-anywhere-3s-b037_c195", "card": { "attribute": "MACRO_PLAYBACK", "name": "ASSIGNMENT_NAME_CHANGE_POINTER_SPEED" } },
            { "slotId": "mx-anywhere-3s-b037_c83", "card": { "attribute": "MACRO_PLAYBACK", "name": "ASSIGNMENT_NAME_UNDO",
              "macro": { "type": "KEYSTROKE", "keystroke": { "code": 29, "modifiers": [ 224 ] } } } },
            { "slotId": "mx-anywhere-3s-b037_c86", "card": { "attribute": "MACRO_PLAYBACK", "name": "ASSIGNMENT_NAME_UNDO",
              "macro": { "type": "KEYSTROKE", "actionName": "Ctrl + Z", "keystroke": { "code": 29, "modifiers": [ 224 ] } } } }
          ] }
        }
        """;

    [Fact]
    public void Merge_ReplacesOnlySelectedParts_SameButtonInOtherNotation()
    {
        var target = TestConfigs.Sample();
        var source = new RingMouseConfig();
        source.Rings["main"] = new RingDefinition { Segments = [Seg("A"), Seg("B")] };
        source.Rings["extra"] = new RingDefinition { Segments = [Seg("C"), Seg("D")] };
        source.Buttons["c253"] = new KeysAction { Keys = "Ctrl+Z" }; // dieselbe Taste wie "0x00FD"
        source.Profiles.Add(new ProfileDefinition { Name = "Excel", Processes = ["excel.exe"] });
        source.General.Language = UiLanguage.German;

        var merged = ConfigMerge.Apply(target, source, ImportParts.Rings | ImportParts.Buttons);
        Assert.Equal(2, merged.Rings["main"].Segments.Count);
        Assert.Contains("extra", merged.Rings.Keys);
        Assert.Contains("apps", merged.Rings.Keys);                    // nicht importierte Ringe bleiben
        Assert.DoesNotContain("0x00FD", merged.Buttons.Keys);
        Assert.Equal("Ctrl+Z", Assert.IsType<KeysAction>(merged.Buttons["c253"]).Keys);
        Assert.Empty(merged.Profiles);                                  // nicht gewählt
        Assert.Equal(UiLanguage.Auto, merged.General.Language);

        var settingsOnly = ConfigMerge.Apply(target, source, ImportParts.Settings);
        Assert.Equal(UiLanguage.German, settingsOnly.General.Language);
        Assert.Equal(8, settingsOnly.Rings["main"].Segments.Count);
    }

    [Fact]
    public void RingMouseFile_RoundTrip_AndInvalidFile()
    {
        var result = RingMouseFileImporter.Load(ConfigSerializer.Serialize(TestConfigs.Sample()), "export.json");
        Assert.Equal(ImportParts.Rings | ImportParts.Buttons | ImportParts.Devices | ImportParts.Settings, result.Available);
        Assert.Equal(3, result.Config.Rings.Count);
        Assert.Throws<InvalidDataException>(() => RingMouseFileImporter.Load("{ kaputt", "x.json"));
    }

    [Theory]
    [InlineData("Windows+Period___269225996___Win+.___win-190", "Win+.")]
    [InlineData("ControlOrCommand+Shift+KeyS___4108___Ctrl+Shift+S___win-83", "Ctrl+Shift+S")]
    [InlineData("Insert___269222921___Insert___", "Insert")]
    [InlineData("Alt+ArrowLeft___0___Alt+Left___win-37", "Alt+Left")]
    [InlineData("ControlOrCommand+Digit5___0___Ctrl+5___win-53", "Ctrl+5")]
    public void Keys_FromLoupedeck(string parameter, string expected)
    {
        Assert.True(OptionsPlusKeys.TryFromKeyboardKey(parameter, out var keys, out var macOnly));
        Assert.False(macOnly);
        Assert.Equal(expected, keys);
    }

    [Fact]
    public void Keys_MacShortcutsAreRejected_HidAndDisplayNamesConverted()
    {
        Assert.False(OptionsPlusKeys.TryFromKeyboardKey("ControlOrCommand+Control+KeyQ___4108___Cmd+Ctrl+Q___mac-12", out _, out var macOnly));
        Assert.True(macOnly);

        Assert.True(OptionsPlusKeys.TryFromHid(27, [224], out var keys));
        Assert.Equal("Ctrl+X", keys);
        Assert.True(OptionsPlusKeys.TryFromHid(0x3E, [], out keys));
        Assert.Equal("F5", keys);
        Assert.True(OptionsPlusKeys.TryFromHid(0x29, [0xE0, 0xE1], out keys));
        Assert.Equal("Ctrl+Shift+Esc", keys);
        Assert.False(OptionsPlusKeys.TryFromHid(0xFF, [], out _));

        Assert.True(OptionsPlusKeys.TryFromDisplay("Ctrl + C", out keys));
        Assert.Equal("Ctrl+C", keys);
        Assert.True(OptionsPlusKeys.TryFromDisplay("Strg + Umschalt + S", out keys));
        Assert.Equal("Ctrl+Shift+S", keys);
    }

    [Fact]
    public void ActionsRing_MainPage_Folder_Macro_PluginActions()
    {
        var result = new ImportResult("test");
        Assert.Equal(5, ActionsRingImporter.ImportRing(RingProfile, "main", result));

        var main = result.Config.Rings["main"].Segments;
        Assert.Equal(8, main.Count);
        Assert.Equal(MediaKey.PlayPause, Assert.IsType<MediaAction>(main[0]!.Action).Key);
        Assert.Equal("Play/Pause", main[0]!.Label);
        Assert.Equal("snippets", Assert.IsType<SubmenuAction>(main[1]!.Action).Ring);
        Assert.Equal("Alt+Shift+B", Assert.IsType<AppKeysAction>(main[2]!.Action).Keys);
        Assert.Equal(("Save as", "Ctrl+Shift+S"), (main[3]!.Label, Assert.IsType<KeysAction>(main[3]!.Action).Keys));
        Assert.Equal(("Example", "https://example.org"), (main[4]!.Label, Assert.IsType<LaunchAction>(main[4]!.Action).Target));
        Assert.Null(main[5]); // macOS-Kürzel
        Assert.Null(main[6]); // leer
        Assert.Null(main[7]); // Excel-Plugin

        var folder = result.Config.Rings["snippets"];
        Assert.Equal("Snippets", folder.Title);
        var snippet = Assert.IsType<SnippetAction>(folder.Segments[0]!.Action);
        Assert.Equal(("Hello world", SnippetMode.Paste), (snippet.Text, snippet.Mode));
        Assert.Equal(SystemCommand.Lock, Assert.IsType<SystemAction>(folder.Segments[1]!.Action).Command);

        Assert.Equal(2, result.Notes.Count(n => n.Kind == ImportNoteKind.Skipped));
        Assert.StartsWith("Test Profile: ring \"main\"", result.Notes[0].Text); // Hauptring zuerst
        Assert.DoesNotContain(ConfigValidator.Validate(result.Config), i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void ActionsRing_OtherDevicesAndTooFewActions_CreateNothing()
    {
        var keypad = new ImportResult("test");
        Assert.Equal(0, ActionsRingImporter.ImportRing(RingProfile.Replace("Loupedeck72", "Loupedeck70"), "main", keypad));
        Assert.Empty(keypad.Config.Rings);

        var strict = new ImportResult("test");
        Assert.Equal(5, ActionsRingImporter.ImportRing(RingProfile, "main-x", strict, minActions: 6));
        Assert.Empty(strict.Config.Rings);
        Assert.DoesNotContain(strict.Notes, n => n.Kind == ImportNoteKind.Imported);
    }

    [Fact]
    public void OptionsPlus_Buttons_AppProfiles_Rings()
    {
        var excelRing = RingProfile.Replace("$DefaultWin___MediaPlayPause", "$Excel___Chart").Replace("$Spotify___ToggleLike", "$Excel___Paste")
            .Replace("$@Generic___@ProfileAction___AAAA", "$Excel___Copy").Replace("$@Generic___@ProfileAction___BBBB", "$Excel___Cut");
        using var importer = new OptionsPlusImporter(Settings,
        [
            new OptionsPlusRingSource("@_defaultwin", "Default", null, RingProfile),
            new OptionsPlusRingSource("@_excel", "Excel", null, excelRing), // nur noch 1 übertragbare Aktion
        ]);

        var mouse = Assert.Single(importer.Mice);
        Assert.Equal(("mx-anywhere-3s-b037", "MX Anywhere 3S"), (mouse.SlotPrefix, mouse.DisplayName));

        var result = importer.Build(mouse.SlotPrefix);
        var c = result.Config;
        Assert.Equal("main", Assert.IsType<OpenRingAction>(c.Buttons["0x00C3"]).Ring);
        Assert.Equal("Ctrl+Z", Assert.IsType<KeysAction>(c.Buttons["0x0056"]).Keys);
        Assert.DoesNotContain("0x0053", c.Buttons.Keys); // Zurück = Originalfunktion

        var excel = Assert.Single(c.Profiles);
        Assert.Equal(["excel.exe"], excel.Processes);
        Assert.Equal("Ctrl+Z", Assert.IsType<KeysAction>(excel.Buttons["0x0053"]).Keys);
        Assert.Single(excel.Buttons);               // gleich wie Standard → geerbt
        Assert.Empty(excel.Rings);                  // App-Ring mit nur einer Aktion verworfen
        Assert.DoesNotContain("main-excel", c.Rings.Keys);
        Assert.Contains(result.Notes, n => n.Kind == ImportNoteKind.Skipped && n.Text.Contains("Change pointer speed"));

        Assert.DoesNotContain("not-to-be-read", ConfigSerializer.Serialize(c));
        Assert.DoesNotContain(ConfigValidator.Validate(ConfigMerge.Apply(TestConfigs.Sample(), c, ImportParts.Rings | ImportParts.Buttons | ImportParts.Profiles)),
            i => i.Severity == IssueSeverity.Error);
    }

    [Theory]
    [InlineData("mx-vertical-eb020", "MX Vertical")]
    [InlineData("m720-triathlon-6b015", "M720 Triathlon")]
    [InlineData("mx-master-3s-b034", "MX Master 3S")]
    public void OptionsPlus_DeviceNames(string prefix, string expected) => Assert.Equal(expected, OptionsPlusImporter.DeviceName(prefix));

    private static RingSegment Seg(string label) => new() { Label = label, Action = new KeysAction { Keys = "Ctrl+C" } };
}
