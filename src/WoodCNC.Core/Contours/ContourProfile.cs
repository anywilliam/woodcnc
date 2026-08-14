namespace WoodCNC.Core.Contours;

public sealed record ContourProfile
{
    public required ContourSourceType SourceType { get; init; }
    public required string SourceFile { get; init; }
    public required IReadOnlyList<ContourPoint> Points { get; init; }
    public required ContourBounds Bounds { get; init; }
    public double ScaleX { get; init; } = 1;
    public double ScaleY { get; init; } = 1;
    public bool IsConfirmed { get; init; }
    public bool IsClosed { get; init; }
    public IReadOnlyList<string> Issues { get; init; } = Array.Empty<string>();

    public double Width => Bounds.Width;
    public double Height => Bounds.Height;
    public bool CanUseForGeneration => IsClosed && IsConfirmed && Issues.Count == 0 && Points.Count > 2;
}
