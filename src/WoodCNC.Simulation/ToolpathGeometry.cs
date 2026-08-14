using WoodCNC.Core.Geometry;
using WoodCNC.Core.Toolpaths;

namespace WoodCNC.Simulation;

public static class ToolpathGeometry
{
    public static Bounds2D GetBounds(IEnumerable<ToolpathSegment> segments)
    {
        var points = segments
            .Where(x => x.IsMotion)
            .SelectMany(x => new[] { x.Start, x.End });

        return Bounds2D.FromPoints(points);
    }

    public static double GetLength(ToolpathSegment segment)
    {
        return segment.Kind switch
        {
            ToolpathSegmentKind.Rapid or ToolpathSegmentKind.Linear => segment.Start.DistanceTo(segment.End),
            ToolpathSegmentKind.ArcClockwise or ToolpathSegmentKind.ArcCounterClockwise => GetArcLength(segment),
            _ => 0
        };
    }

    private static double GetArcLength(ToolpathSegment segment)
    {
        if (segment.Center is null || segment.Radius is null)
        {
            return 0;
        }

        var center = segment.Center.Value;
        if (segment.Start.DistanceToXy(segment.End) <= double.Epsilon)
        {
            return Math.PI * 2 * segment.Radius.Value;
        }

        var startAngle = Math.Atan2(segment.Start.Y - center.Y, segment.Start.X - center.X);
        var endAngle = Math.Atan2(segment.End.Y - center.Y, segment.End.X - center.X);
        var sweep = endAngle - startAngle;

        if (segment.Kind == ToolpathSegmentKind.ArcClockwise && sweep > 0)
        {
            sweep -= Math.PI * 2;
        }
        else if (segment.Kind == ToolpathSegmentKind.ArcCounterClockwise && sweep < 0)
        {
            sweep += Math.PI * 2;
        }

        var xyLength = Math.Abs(sweep) * segment.Radius.Value;
        var zDelta = segment.End.Z - segment.Start.Z;
        return Math.Sqrt(xyLength * xyLength + zDelta * zDelta);
    }
}

