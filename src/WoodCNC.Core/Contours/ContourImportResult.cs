namespace WoodCNC.Core.Contours;

public sealed record ContourImportResult
{
    public required bool Success { get; init; }
    public required IReadOnlyList<ContourProfile> Profiles { get; init; }
    public int? RecommendedIndex { get; init; }
    public ContourPlane Plane { get; init; } = ContourPlane.XY;
    public ContourWorkpieceKind WorkpieceKind { get; init; } = ContourWorkpieceKind.RotatingWorkpieceTopView;
    public ContourBounds? GroupBounds { get; init; }
    public ContourOrigin? Origin { get; init; }
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public ContourProfile? RecommendedProfile =>
        RecommendedIndex is int index && index >= 0 && index < Profiles.Count
            ? Profiles[index]
            : null;

    public bool CanUseForWorkpieceGeneration => Success && Profiles.Count > 0 && GroupBounds is not null && Origin is not null;
}
