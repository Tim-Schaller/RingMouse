using RingMouse.Core.Config;
using Xunit;

namespace RingMouse.Core.Tests;

/// <summary>Farbangaben und Aussehen-Einstellungen des Rings (Config-Seite).</summary>
public class AppearanceTests
{
    [Theory]
    [InlineData("#0078D4", 0xFF, 0x00, 0x78, 0xD4)]
    [InlineData("0078d4", 0xFF, 0x00, 0x78, 0xD4)]
    [InlineData(" #80E81123 ", 0x80, 0xE8, 0x11, 0x23)]
    public void ColorValue_ParsesHex(string text, byte a, byte r, byte g, byte b)
    {
        Assert.True(ColorValue.TryParse(text, out var c));
        Assert.Equal(new ColorValue(a, r, g, b), c);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("blau")]
    [InlineData("#GG0000")]
    public void ColorValue_RejectsInvalid(string? text) => Assert.False(ColorValue.TryParse(text, out _));

    [Fact]
    public void ColorValue_RoundTrips()
    {
        Assert.Equal("#0078D4", ColorValue.FromRgb(0x00, 0x78, 0xD4).ToString());
        Assert.Equal("#80E81123", new ColorValue(0x80, 0xE8, 0x11, 0x23).ToString());
    }

    [Fact]
    public void Defaults_FollowTheSystem()
    {
        var ring = DefaultConfig.Create().Ring;
        Assert.Null(ring.RingColor);
        Assert.Null(ring.AccentColor);
        Assert.Null(ring.PointerColor);
        Assert.Equal(100, ring.Opacity);
        Assert.Equal(100, ring.TextScale);
    }

    [Fact]
    public void Validator_WarnsOnBadColor_RejectsOutOfRange()
    {
        var config = DefaultConfig.Create();
        config.Ring.AccentColor = "blau";
        config.Ring.RingColor = "#1E2A3A";
        config.Ring.Opacity = 10;
        config.Ring.PointerSize = 80;
        var issues = ConfigValidator.Validate(config);

        Assert.Contains(issues, i => i.Path == "ring.accentColor" && i.Severity == IssueSeverity.Warning);
        Assert.DoesNotContain(issues, i => i.Path == "ring.ringColor");
        Assert.Contains(issues, i => i.Path == "ring.opacity" && i.Severity == IssueSeverity.Error);
        Assert.Contains(issues, i => i.Path == "ring.pointerSize" && i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void Appearance_SurvivesSaveAndLoad()
    {
        var config = DefaultConfig.Create();
        config.Ring.RingColor = "#1E2A3A";
        config.Ring.AccentColor = "#FFB900";
        config.Ring.Opacity = 85;
        config.Ring.TextScale = 130;
        config.Ring.PointerColor = "#E81123";
        config.Ring.PointerSize = 14;

        var json = ConfigSerializer.Serialize(config);
        Assert.Contains("\"accentColor\": \"#FFB900\"", json);
        var back = ConfigSerializer.Deserialize(json);
        Assert.Equal("#1E2A3A", back.Ring.RingColor);
        Assert.Equal(85, back.Ring.Opacity);
        Assert.Equal(130, back.Ring.TextScale);
        Assert.Equal(14, back.Ring.PointerSize);
    }
}
