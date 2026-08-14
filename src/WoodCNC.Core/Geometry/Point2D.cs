namespace WoodCNC.Core.Geometry;

public readonly record struct Point2D(double X, double Y, double Z = 0)
{
    public double DistanceTo(Point2D other)
    {
        var dx = other.X - X;
        var dy = other.Y - Y;
        var dz = other.Z - Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    public double DistanceToXy(Point2D other)
    {
        var dx = other.X - X;
        var dy = other.Y - Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

