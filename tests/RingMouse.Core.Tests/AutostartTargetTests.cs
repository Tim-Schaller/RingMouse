using RingMouse.Core.Autostart;
using Xunit;

namespace RingMouse.Core.Tests;

/// <summary>Autostart/Startmenü: Erkennen, ob ein Eintrag noch die laufende Exe startet (z.B. nach einem Umzug).</summary>
public class AutostartTargetTests
{
    private const string Installed = @"C:\Users\x\AppData\Local\Programs\RingMouse\RingMouse.exe";
    private const string OldPath = @"C:\Users\x\Downloads\RingMouse\publish\RingMouse\RingMouse.exe";

    [Theory]
    [InlineData("\"C:\\Program Files\\RingMouse\\RingMouse.exe\" --autostart", @"C:\Program Files\RingMouse\RingMouse.exe")]
    [InlineData("C:\\Program Files\\RingMouse\\RingMouse.exe --autostart", @"C:\Program Files\RingMouse\RingMouse.exe")]
    [InlineData("C:\\Tools\\RingMouse.EXE", @"C:\Tools\RingMouse.EXE")]
    [InlineData("  \"C:\\a b\\RingMouse.exe\"  ", @"C:\a b\RingMouse.exe")]
    [InlineData("C:\\Tools\\ringmouse --autostart", @"C:\Tools\ringmouse")]
    public void ExeFromCommandLine_FindsTheExecutable(string commandLine, string expected) =>
        Assert.Equal(expected, AutostartTarget.ExeFromCommandLine(commandLine));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"")]
    [InlineData("\"\" --autostart")]
    public void ExeFromCommandLine_ReturnsNullForGarbage(string? commandLine) =>
        Assert.Null(AutostartTarget.ExeFromCommandLine(commandLine));

    [Fact]
    public void Classify_SameExe_IsCurrent_RegardlessOfCaseAndQuotes() =>
        Assert.Equal(AutostartTargetState.Current,
            AutostartTarget.Classify("\"" + Installed.ToUpperInvariant() + "\"", Installed, _ => throw new InvalidOperationException("not needed")));

    [Fact]
    public void Classify_MovedExe_IsMissing() =>
        Assert.Equal(AutostartTargetState.Missing, AutostartTarget.Classify(OldPath, Installed, _ => false));

    [Fact]
    public void Classify_OtherExistingCopy_IsLeftAlone() =>
        Assert.Equal(AutostartTargetState.OtherCopy, AutostartTarget.Classify(OldPath, Installed, p => p == OldPath));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Classify_UnreadableEntry_IsMissing(string? registered) =>
        Assert.Equal(AutostartTargetState.Missing, AutostartTarget.Classify(registered, Installed, _ => true));

    [Fact]
    public void SamePath_NormalizesRelativeParts() =>
        Assert.True(AutostartTarget.SamePath(@"C:\Programs\RingMouse\..\RingMouse\RingMouse.exe", @"c:\programs\ringmouse\ringmouse.exe"));
}
