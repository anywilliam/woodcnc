namespace WoodCNC.Core.Contours;

public sealed record ContourSegment
{
    public required ContourSegmentKind Kind { get; init; }
    public required ContourPoint Start { get; init; }
    public required ContourPoint End { get; init; }
    public required IReadOnlyList<ContourPoint> Points { get; init; }
    public string SourceEntityType { get; init; } = string.Empty;
    public int SourceEntityIndex { get; init; }

    public ContourSegment Reversed()
    {
        var points = Points.Reverse().ToArray();
        return this with
        {
            Start = End,
            End = Start,
            Points = points
        };
    }
}
