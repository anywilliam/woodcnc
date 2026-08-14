using WoodCNC.Core.Contours;

namespace WoodCNC.Contour.Templates;

public sealed class ContourTemplateGenerator
{
    private const int CurveSegments = 96;

    public ContourProfile Generate(ContourTemplateKind kind, double width, double height, double cornerRadius = 0)
    {
        var issues = Validate(kind, width, height, cornerRadius);
        if (issues.Count > 0)
        {
            return CreateProfile(kind, Array.Empty<ContourPoint>(), issues);
        }

        var points = kind switch
        {
            ContourTemplateKind.Rectangle => Rectangle(width, height),
            ContourTemplateKind.Circle => Ellipse(width, width),
            ContourTemplateKind.Ellipse => Ellipse(width, height),
            ContourTemplateKind.RoundedRectangle => RoundedRectangle(width, height, cornerRadius),
            _ => Array.Empty<ContourPoint>()
        };

        return CreateProfile(kind, points, Array.Empty<string>());
    }

    private static IReadOnlyList<string> Validate(ContourTemplateKind kind, double width, double height, double cornerRadius)
    {
        var issues = new List<string>();
        if (width <= 0)
        {
            issues.Add("宽度必须大于 0。");
        }

        if (height <= 0)
        {
            issues.Add("高度必须大于 0。");
        }

        if (kind == ContourTemplateKind.Circle && Math.Abs(width - height) > 0.001)
        {
            issues.Add("圆形模板宽高必须一致。");
        }

        if (kind == ContourTemplateKind.RoundedRectangle)
        {
            if (cornerRadius < 0)
            {
                issues.Add("圆角半径不能小于 0。");
            }
            else if (cornerRadius * 2 > Math.Min(width, height))
            {
                issues.Add("圆角半径不能超过短边的一半。");
            }
        }

        return issues;
    }

    private static ContourProfile CreateProfile(ContourTemplateKind kind, IReadOnlyList<ContourPoint> points, IReadOnlyList<string> issues)
    {
        return new ContourProfile
        {
            SourceType = ContourSourceType.Template,
            SourceFile = kind.ToString(),
            Points = points,
            Bounds = ContourBounds.FromPoints(points),
            IsClosed = points.Count > 2 && points[0].X == points[^1].X && points[0].Y == points[^1].Y,
            IsConfirmed = issues.Count == 0,
            Issues = issues
        };
    }

    private static IReadOnlyList<ContourPoint> Rectangle(double width, double height)
    {
        var halfWidth = width / 2;
        var halfHeight = height / 2;
        return new[]
        {
            new ContourPoint(-halfWidth, -halfHeight),
            new ContourPoint(halfWidth, -halfHeight),
            new ContourPoint(halfWidth, halfHeight),
            new ContourPoint(-halfWidth, halfHeight),
            new ContourPoint(-halfWidth, -halfHeight)
        };
    }

    private static IReadOnlyList<ContourPoint> Ellipse(double width, double height)
    {
        var points = new List<ContourPoint>(CurveSegments + 1);
        var radiusX = width / 2;
        var radiusY = height / 2;
        for (var i = 0; i <= CurveSegments; i++)
        {
            var angle = Math.PI * 2 * i / CurveSegments;
            points.Add(new ContourPoint(Math.Cos(angle) * radiusX, Math.Sin(angle) * radiusY));
        }

        if (points.Count > 0)
        {
            points.Add(points[0]);
        }

        return points;
    }

    private static IReadOnlyList<ContourPoint> RoundedRectangle(double width, double height, double radius)
    {
        if (radius <= 0)
        {
            return Rectangle(width, height);
        }

        var points = new List<ContourPoint>();
        var halfWidth = width / 2;
        var halfHeight = height / 2;
        AddCorner(points, halfWidth - radius, halfHeight - radius, radius, 0, Math.PI / 2);
        AddCorner(points, -halfWidth + radius, halfHeight - radius, radius, Math.PI / 2, Math.PI);
        AddCorner(points, -halfWidth + radius, -halfHeight + radius, radius, Math.PI, Math.PI * 1.5);
        AddCorner(points, halfWidth - radius, -halfHeight + radius, radius, Math.PI * 1.5, Math.PI * 2);
        points.Add(points[0]);
        return points;
    }

    private static void AddCorner(List<ContourPoint> points, double centerX, double centerY, double radius, double startAngle, double endAngle)
    {
        const int cornerSegments = 16;
        for (var i = 0; i <= cornerSegments; i++)
        {
            var angle = startAngle + (endAngle - startAngle) * i / cornerSegments;
            points.Add(new ContourPoint(centerX + Math.Cos(angle) * radius, centerY + Math.Sin(angle) * radius));
        }
    }
}
