using WoodCNC.Core.Toolpaths;

namespace WoodCNC.Core.GCode;

public sealed record GCodeProgram
{
    public required string SourceName { get; init; }
    public required string OriginalText { get; init; }
    public required IReadOnlyList<ToolpathSegment> Segments { get; init; }
    public required IReadOnlyList<ParseDiagnostic> Diagnostics { get; init; }

    public int MotionCount => Segments.Count(x => x.IsMotion);
    public int UnknownCount => Diagnostics.Count;
}

