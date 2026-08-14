namespace WoodCNC.IO;

public sealed record ImportedGCodeFile(
    string Path,
    string Text,
    bool WasXorDecoded,
    string Message);

