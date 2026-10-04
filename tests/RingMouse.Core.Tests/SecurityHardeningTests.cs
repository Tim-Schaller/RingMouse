using RingMouse.Core.Config;
using Xunit;

namespace RingMouse.Core.Tests;

/// <summary>Regressionstests für die Security-Härtung: Log-Redaktion (S3) und Import-Warnung (S6).</summary>
public class SecurityHardeningTests
{
    // --- S3: DescribeForLog() darf keine sensiblen Inhalte preisgeben --------------------------------

    [Fact]
    public void SnippetDescribeForLog_HidesText_ButKeepsLength()
    {
        const string secret = "my-super-secret-password-9000";
        var action = new SnippetAction { Text = secret };

        var log = action.DescribeForLog();

        Assert.DoesNotContain("secret", log, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", log, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains(secret.Length.ToString(), log);
        // Die UI-Beschreibung darf den Inhalt weiterhin zeigen (der Nutzer sieht seine eigene Config).
        Assert.Contains("secret", action.Describe(), System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PowerShellDescribeForLog_HidesInlineCommand()
    {
        var action = new PowerShellAction { Command = "Invoke-WebRequest -Headers @{Authorization='Bearer TOP-SECRET'}" };

        var log = action.DescribeForLog();

        Assert.DoesNotContain("SECRET", log, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", log, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PowerShellDescribeForLog_KeepsScriptPath()
    {
        var action = new PowerShellAction { Script = @"C:\tools\deploy.ps1" };

        Assert.Contains(@"C:\tools\deploy.ps1", action.DescribeForLog());
    }

    [Fact]
    public void LaunchDescribeForLog_HidesArguments_ButKeepsTarget()
    {
        var action = new LaunchAction { Target = "rclone.exe", Arguments = "--password HUNTER2 sync a b" };

        var log = action.DescribeForLog();

        Assert.DoesNotContain("HUNTER2", log);
        Assert.Contains("rclone.exe", log);
    }

    // --- S6: ConfigInspection findet ausführende Aktionen, auch in Sequenzen -------------------------

    [Fact]
    public void CountExecutable_FindsLaunchAndPowerShell_AcrossButtonsRingsProfilesAndSequences()
    {
        var config = new RingMouseConfig();
        config.Buttons["0x00FD"] = new LaunchAction { Target = "calc.exe" };
        config.Rings["main"] = new RingDefinition
        {
            Segments =
            [
                new RingSegment { Action = new PowerShellAction { Command = "Get-Date", Elevated = true } },
                new RingSegment { Action = new KeysAction { Keys = "Ctrl+C" } },
                null,
            ],
        };
        config.Profiles.Add(new ProfileDefinition
        {
            Name = "p",
            Buttons = { ["0x0052"] = new SequenceAction { Steps = [new DelayAction { Ms = 10 }, new LaunchAction { Target = "notepad.exe" }] } },
        });

        var result = ConfigInspection.CountExecutable(config);

        Assert.Equal(2, result.Launch);      // calc.exe (Taste) + notepad.exe (in der Sequenz)
        Assert.Equal(1, result.PowerShell);  // im Ring-Segment
        Assert.True(result.AnyElevated);     // das PowerShell-Segment
        Assert.Equal(3, result.Total);
    }

    [Fact]
    public void CountExecutable_HarmlessConfig_IsZero()
    {
        var config = new RingMouseConfig();
        config.Buttons["0x00FD"] = new OpenRingAction { Ring = "main" };
        config.Rings["main"] = new RingDefinition
        {
            Segments = [new RingSegment { Action = new MediaAction { Key = MediaKey.PlayPause } }, new RingSegment { Action = new KeysAction { Keys = "Ctrl+V" } }],
        };

        var result = ConfigInspection.CountExecutable(config);

        Assert.Equal(0, result.Total);
        Assert.False(result.AnyElevated);
    }
}
