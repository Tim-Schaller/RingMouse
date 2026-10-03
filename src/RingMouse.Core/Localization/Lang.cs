using System.Globalization;
using RingMouse.Core.Config;

namespace RingMouse.Core.Localization;

/// <summary>
/// Zweisprachige Oberfläche (Englisch/Deutsch). Texte stehen direkt am Verwendungsort als Paar
/// <c>L("English", "Deutsch")</c> (mit <c>using static RingMouse.Core.Localization.Lang;</c>) – so kann keine
/// Übersetzung fehlen. Logs, Schema-Beschreibungen und technische Meldungen sind immer englisch.
/// </summary>
public static class Lang
{
    private static volatile bool s_german = WindowsIsGerman();

    /// <summary>true = Deutsch, sonst Englisch.</summary>
    public static bool IsGerman => s_german;

    /// <summary>Sprache festlegen – beim Start aus der Config; Auto folgt der Windows-Anzeigesprache.</summary>
    public static void Apply(UiLanguage language) => s_german = language switch
    {
        UiLanguage.German => true,
        UiLanguage.English => false,
        _ => WindowsIsGerman(),
    };

    /// <summary>Text in der aktuellen Sprache.</summary>
    public static string L(string english, string german) => s_german ? german : english;

    /// <summary>Kultur für Zahlen und Datumsangaben in Oberflächentexten.</summary>
    public static CultureInfo Culture => s_german ? CultureInfo.GetCultureInfo("de-DE") : CultureInfo.GetCultureInfo("en-US");

    private static bool WindowsIsGerman() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("de", StringComparison.OrdinalIgnoreCase);
}
