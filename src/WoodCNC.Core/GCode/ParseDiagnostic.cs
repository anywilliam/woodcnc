namespace WoodCNC.Core.GCode;

public sealed record ParseDiagnostic(int LineNumber, string Message, string OriginalLine);

