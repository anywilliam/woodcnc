namespace WoodCNC.Core.Geometry;

public readonly record struct Bounds2D(double MinX, double MinY, double MaxX, double MaxY)
{
    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;

    public static Bounds2D Empty { get; } = new(0, 0, 0, 0);

    public static Bounds2D FromPoints(IEnumerable<Point2D> points)
    {
        using var enumerator = points.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            return Empty;
        }

        var minX = enumerator.Current.X;
        var maxX = enumerator.Current.X;
        var minY = enumerator.Current.Y;
        var maxY = enumerator.Current.Y;

        while (enumerator.MoveNext())
        {
            minX = Math.Min(minX, enumerator.Current.X);
            maxX = Math.Max(maxX, enumerator.Current.X);
            minY = Math.Min(minY, enumerator.Current.Y);
            maxY = Math.Max(maxY, enumerator.Current.Y);
        }

        return new Bounds2D(minX, minY, maxX, maxY);
    }
}

