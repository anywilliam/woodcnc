namespace WoodCNC.Core.Contours;

public readonly record struct ContourBounds(double MinX, double MinY, double MaxX, double MaxY)
{
    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;

    public static ContourBounds Empty { get; } = new(0, 0, 0, 0);

    public static ContourBounds FromPoints(IEnumerable<ContourPoint> points)
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

        return new ContourBounds(minX, minY, maxX, maxY);
    }
}
