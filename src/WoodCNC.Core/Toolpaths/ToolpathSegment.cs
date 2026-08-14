using WoodCNC.Core.Geometry;

namespace WoodCNC.Core.Toolpaths;

public sealed record ToolpathSegment
{
    public required ToolpathSegmentKind Kind { get; init; }
    public Point2D Start { get; init; }
    public Point2D End { get; init; }
    public Point2D? Center { get; init; }
    public double? Radius { get; init; }
    public double? FeedRate { get; init; }
    public double? DwellSeconds { get; init; }
    public required string OriginalLine { get; init; }

    public bool IsMotion => Kind is ToolpathSegmentKind.Rapid
        or ToolpathSegmentKind.Linear
        or ToolpathSegmentKind.ArcClockwise
        or ToolpathSegmentKind.ArcCounterClockwise;
}

