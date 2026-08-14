namespace WoodCNC.Core.Contours;

public sealed record ImageSuitabilityReport
{
    public required ImageSuitabilityStatus Status { get; init; }
    public double ContrastScore { get; init; }
    public double SubjectWidthRatio { get; init; }
    public double SubjectHeightRatio { get; init; }
    public double SubjectAreaRatio { get; init; }
    public double MinMarginRatio { get; init; }
    public int SubjectCount { get; init; }
    public int ImageWidth { get; init; }
    public int ImageHeight { get; init; }
    public IReadOnlyList<string> Issues { get; init; } = Array.Empty<string>();
}
