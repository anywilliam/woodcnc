namespace WoodCNC.Core.Contours;

public readonly record struct ContourPoint(double X, double Y, bool IsBreak = false, bool IsControlPoint = false);
