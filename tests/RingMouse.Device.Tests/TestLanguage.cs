using System.Runtime.CompilerServices;
using RingMouse.Core.Config;
using RingMouse.Core.Localization;

namespace RingMouse.Device.Tests;

internal static class TestLanguage
{
    /// <summary>Tests laufen immer auf Englisch – unabhängig von der Windows-Sprache des Rechners.</summary>
#pragma warning disable CA2255 // Modulinitialisierer in einer Testbibliothek ist hier gewollt
    [ModuleInitializer]
    internal static void UseEnglish() => Lang.Apply(UiLanguage.English);
#pragma warning restore CA2255
}
