using WoodCNC.Core.Contours;

namespace WoodCNC.Contour;

public sealed class ContourScaler
{
    public ContourProfile ConfirmSize(ContourProfile profile, double targetWidth, double targetHeight)
    {
        if (targetWidth <= 0 || targetHeight <= 0)
        {
            return profile with
            {
                IsConfirmed = false,
                Issues = profile.Issues.Concat(new[] { "确认尺寸必须大于 0。" }).ToArray()
            };
        }

        if (profile.Width <= 0 || profile.Height <= 0)
        {
            return profile with
            {
                IsConfirmed = false,
                Issues = profile.Issues.Concat(new[] { "轮廓宽高无效，不能换算尺寸。" }).ToArray()
            };
        }

        var scaleX = targetWidth / profile.Width;
        var scaleY = targetHeight / profile.Height;
        var scaledPoints = profile.Points
            .Select(point => new ContourPoint(point.X * scaleX, point.Y * scaleY, point.IsBreak, point.IsControlPoint))
            .ToArray();

        return profile with
        {
            Points = scaledPoints,
            Bounds = ContourBounds.FromPoints(scaledPoints),
            ScaleX = scaleX,
            ScaleY = scaleY,
            IsConfirmed = true
        };
    }
}
