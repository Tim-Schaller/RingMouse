namespace RingMouse.Core.Ring;

/// <summary>
/// Geometrie des Rings: Auswahl über den Winkel (nicht über Hit-Test). Segment 0 ist oben zentriert,
/// danach im Uhrzeigersinn. Koordinaten: x nach rechts, y nach unten (Bildschirm).
/// </summary>
public static class RingGeometry
{
    /// <summary>Winkel in Grad: 0 = oben, 90 = rechts, 180 = unten, 270 = links.</summary>
    public static double AngleDegrees(double dx, double dy)
    {
        var angle = Math.Atan2(dx, -dy) * 180.0 / Math.PI;
        return angle < 0 ? angle + 360.0 : angle;
    }

    /// <summary>Segmentindex für einen Versatz vom Mittelpunkt, -1 in der Deadzone.</summary>
    public static int SegmentAt(double dx, double dy, int segmentCount, double deadzone)
    {
        if (segmentCount <= 0) return -1;
        if (dx * dx + dy * dy < deadzone * deadzone) return -1;
        var span = 360.0 / segmentCount;
        var index = (int)Math.Floor((AngleDegrees(dx, dy) + span / 2.0) / span);
        return index % segmentCount;
    }

    /// <summary>Startwinkel (Grad) von Segment <paramref name="index"/>; Segment 0 beginnt bei -span/2.</summary>
    public static double SegmentStartAngle(int index, int segmentCount) => index * (360.0 / segmentCount) - 180.0 / segmentCount;

    public static double SegmentCenterAngle(int index, int segmentCount) => index * (360.0 / segmentCount);

    /// <summary>Punkt auf dem Kreis mit Radius <paramref name="radius"/> für einen Winkel (0 = oben, im Uhrzeigersinn).</summary>
    public static (double X, double Y) PointAt(double angleDegrees, double radius)
    {
        var rad = angleDegrees * Math.PI / 180.0;
        return (Math.Sin(rad) * radius, -Math.Cos(rad) * radius);
    }

    public static double Distance(double dx, double dy) => Math.Sqrt(dx * dx + dy * dy);

    /// <summary>Kleinster Winkelabstand (0–180°) zwischen zwei Richtungen.</summary>
    public static double AngularDistance(double a, double b)
    {
        var d = Math.Abs(a - b) % 360.0;
        return d > 180.0 ? 360.0 - d : d;
    }

    /// <summary>
    /// Verschiebt den Ringmittelpunkt so, dass ein Ring mit <paramref name="halfSize"/> vollständig in
    /// <paramref name="bounds"/> liegt (Clamping an Bildschirmrändern).
    /// </summary>
    public static (double X, double Y) ClampCenter(double x, double y, double halfSize, Bounds bounds)
    {
        var minX = bounds.Left + halfSize;
        var maxX = bounds.Right - halfSize;
        var minY = bounds.Top + halfSize;
        var maxY = bounds.Bottom - halfSize;
        var cx = minX > maxX ? (bounds.Left + bounds.Right) / 2.0 : Math.Clamp(x, minX, maxX);
        var cy = minY > maxY ? (bounds.Top + bounds.Bottom) / 2.0 : Math.Clamp(y, minY, maxY);
        return (cx, cy);
    }
}

public readonly record struct Bounds(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
}
