using RingMouse.Core.Ring;
using Xunit;

namespace RingMouse.Core.Tests;

public class RingGeometryTests
{
    private const double Deadzone = 20;

    [Theory]
    [InlineData(0, -100, 0)]     // oben
    [InlineData(70, -70, 1)]     // oben rechts
    [InlineData(100, 0, 2)]      // rechts
    [InlineData(70, 70, 3)]
    [InlineData(0, 100, 4)]      // unten
    [InlineData(-70, 70, 5)]
    [InlineData(-100, 0, 6)]     // links
    [InlineData(-70, -70, 7)]
    public void EightSegments_ByDirection(double dx, double dy, int expected) =>
        Assert.Equal(expected, RingGeometry.SegmentAt(dx, dy, 8, Deadzone));

    [Theory]
    [InlineData(22.4, 0)]
    [InlineData(22.6, 1)]
    [InlineData(337.6, 0)]
    [InlineData(337.4, 7)]
    [InlineData(359.9, 0)]
    public void EightSegments_Boundaries(double angle, int expected)
    {
        var (x, y) = RingGeometry.PointAt(angle, 100);
        Assert.Equal(expected, RingGeometry.SegmentAt(x, y, 8, Deadzone));
    }

    [Theory]
    [InlineData(4, 100, 0, 1)]
    [InlineData(4, 0, 100, 2)]
    [InlineData(4, -100, 0, 3)]
    [InlineData(6, 100, 0, 1)]   // 60°-Segmente: rechts (90°) liegt in Segment 1 (30–90)? → Grenze 90 gehört zu 2
    [InlineData(5, 0, -100, 0)]
    public void OtherCounts(int count, double dx, double dy, int expected)
    {
        if (count == 6 && dx == 100) expected = 2; // 90° ist die Grenze zwischen 1 und 2 und fällt auf 2
        Assert.Equal(expected, RingGeometry.SegmentAt(dx, dy, count, Deadzone));
    }

    [Fact]
    public void Deadzone_ReturnsMinusOne()
    {
        Assert.Equal(-1, RingGeometry.SegmentAt(5, 5, 8, Deadzone));
        Assert.Equal(-1, RingGeometry.SegmentAt(0, 0, 8, Deadzone));
        Assert.Equal(0, RingGeometry.SegmentAt(0, -20.01, 8, Deadzone));
    }

    [Fact]
    public void AngleDegrees_ScreenCoordinates()
    {
        Assert.Equal(0, RingGeometry.AngleDegrees(0, -1), 6);
        Assert.Equal(90, RingGeometry.AngleDegrees(1, 0), 6);
        Assert.Equal(180, RingGeometry.AngleDegrees(0, 1), 6);
        Assert.Equal(270, RingGeometry.AngleDegrees(-1, 0), 6);
    }

    [Fact]
    public void ClampCenter_KeepsRingOnScreen()
    {
        var screen = new Bounds(0, 0, 1920, 1080);
        Assert.Equal((500.0, 500.0), RingGeometry.ClampCenter(500, 500, 150, screen));
        Assert.Equal((150.0, 150.0), RingGeometry.ClampCenter(10, 20, 150, screen));
        Assert.Equal((1770.0, 930.0), RingGeometry.ClampCenter(1919, 1079, 150, screen));

        // Zweiter Monitor links mit negativen Koordinaten
        var left = new Bounds(-2560, 0, 0, 1440);
        Assert.Equal((-150.0, 700.0), RingGeometry.ClampCenter(-5, 700, 150, left));
    }
}
