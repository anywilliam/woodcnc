using System.IO;
using WoodCNC.Core.Contours;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WoodCNC.Contour.ImageAssist;

public sealed class ImageAssistInspector
{
    public ImageSuitabilityReport Inspect(string path)
    {
        if (!File.Exists(path))
        {
            return Failed("图片文件不存在。");
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        var supported = extension is ".jpg" or ".jpeg" or ".png" or ".bmp";
        if (!supported)
        {
            return Failed("图片格式不支持，首版只接受 jpg/jpeg/png/bmp。");
        }

        ImageData image;
        try
        {
            image = LoadBitmap(path);
        }
        catch (NotSupportedException)
        {
            return Failed("图片无法解码，请确认文件内容是真实 jpg/jpeg/png/bmp 图片。");
        }
        catch (IOException ex)
        {
            return Failed($"图片读取失败: {ex.Message}");
        }

        var analysis = Analyze(image);
        var issues = new List<string>
        {
            "图片辅助入口已建立。图片轮廓不能直接生成 G-code，后续必须经过轮廓确认。"
        };

        if (analysis.ForegroundPixels == 0)
        {
            issues.Add("未识别到明显主体，可能是主体和背景颜色太接近。");
        }
        else
        {
            if (analysis.ContrastScore < 0.12)
            {
                issues.Add("主体和背景对比度偏低，建议提高轮廓线/主体颜色对比。");
            }

            if (analysis.SubjectAreaRatio < 0.01)
            {
                issues.Add("主体占比过小，建议裁剪空白边或放大主体。");
            }
            else if (analysis.SubjectAreaRatio > 0.85)
            {
                issues.Add("主体几乎铺满图片，建议保留少量边距便于识别轮廓。");
            }

            if (analysis.MinMarginRatio < 0.01)
            {
                issues.Add("主体贴近图片边缘，建议保留边距后再导入。");
            }
        }

        return new ImageSuitabilityReport
        {
            Status = analysis.ForegroundPixels == 0 ? ImageSuitabilityStatus.Failed :
                issues.Count == 1 ? ImageSuitabilityStatus.Passed : ImageSuitabilityStatus.NeedsAdjustment,
            ContrastScore = analysis.ContrastScore,
            SubjectWidthRatio = analysis.SubjectWidthRatio,
            SubjectHeightRatio = analysis.SubjectHeightRatio,
            SubjectAreaRatio = analysis.SubjectAreaRatio,
            MinMarginRatio = analysis.MinMarginRatio,
            SubjectCount = analysis.ForegroundPixels > 0 ? 1 : 0,
            ImageWidth = image.Width,
            ImageHeight = image.Height,
            Issues = issues
        };
    }

    private static ImageSuitabilityReport Failed(string issue)
    {
        return new ImageSuitabilityReport
        {
            Status = ImageSuitabilityStatus.Failed,
            Issues = new[] { issue }
        };
    }

    private static ImageData LoadBitmap(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();

        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return new ImageData(converted.PixelWidth, converted.PixelHeight, stride, pixels);
    }

    private static ImageAnalysis Analyze(ImageData image)
    {
        var width = image.Width;
        var height = image.Height;
        var sampleStep = Math.Max(1, Math.Min(width, height) / 300);
        var background = EstimateBackground(image, sampleStep);
        var threshold = 38d;
        var minX = width;
        var minY = height;
        var maxX = -1;
        var maxY = -1;
        var foreground = 0;
        var totalDistance = 0d;
        var samples = 0;

        for (var y = 0; y < height; y += sampleStep)
        {
            for (var x = 0; x < width; x += sampleStep)
            {
                var color = image.GetPixel(x, y);
                var distance = ColorDistance(color, background);
                totalDistance += distance;
                samples++;
                if (color.A > 24 && distance >= threshold)
                {
                    foreground++;
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        if (foreground == 0)
        {
            return new ImageAnalysis(0, totalDistance / Math.Max(samples, 1) / 255d, 0, 0, 0, 0);
        }

        var subjectWidth = Math.Min(width, maxX + sampleStep) - minX;
        var subjectHeight = Math.Min(height, maxY + sampleStep) - minY;
        var margin = Math.Min(Math.Min(minX, minY), Math.Min(width - maxX - 1, height - maxY - 1));

        return new ImageAnalysis(
            foreground,
            totalDistance / Math.Max(samples, 1) / 255d,
            subjectWidth / (double)width,
            subjectHeight / (double)height,
            foreground / (double)samples,
            margin / (double)Math.Min(width, height));
    }

    private static Pixel EstimateBackground(ImageData image, int step)
    {
        var r = 0d;
        var g = 0d;
        var b = 0d;
        var count = 0;

        for (var x = 0; x < image.Width; x += step)
        {
            Add(image.GetPixel(x, 0));
            Add(image.GetPixel(x, image.Height - 1));
        }

        for (var y = 0; y < image.Height; y += step)
        {
            Add(image.GetPixel(0, y));
            Add(image.GetPixel(image.Width - 1, y));
        }

        return new Pixel(
            (int)Math.Clamp(r / Math.Max(count, 1), 0, 255),
            (int)Math.Clamp(g / Math.Max(count, 1), 0, 255),
            (int)Math.Clamp(b / Math.Max(count, 1), 0, 255),
            255);

        void Add(Pixel color)
        {
            var alpha = color.A / 255d;
            r += color.R * alpha + 255 * (1 - alpha);
            g += color.G * alpha + 255 * (1 - alpha);
            b += color.B * alpha + 255 * (1 - alpha);
            count++;
        }
    }

    private static double ColorDistance(Pixel color, Pixel background)
    {
        var alpha = color.A / 255d;
        var r = color.R * alpha + 255 * (1 - alpha);
        var g = color.G * alpha + 255 * (1 - alpha);
        var b = color.B * alpha + 255 * (1 - alpha);
        var dr = r - background.R;
        var dg = g - background.G;
        var db = b - background.B;
        return Math.Sqrt(dr * dr + dg * dg + db * db) / Math.Sqrt(3);
    }

    private sealed record ImageAnalysis(
        int ForegroundPixels,
        double ContrastScore,
        double SubjectWidthRatio,
        double SubjectHeightRatio,
        double SubjectAreaRatio,
        double MinMarginRatio);

    private sealed record ImageData(int Width, int Height, int Stride, byte[] Pixels)
    {
        public Pixel GetPixel(int x, int y)
        {
            var index = y * Stride + x * 4;
            return new Pixel(Pixels[index + 2], Pixels[index + 1], Pixels[index], Pixels[index + 3]);
        }
    }

    private readonly record struct Pixel(int R, int G, int B, int A);
}
