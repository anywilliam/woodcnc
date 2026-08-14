using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using WoodCNC.Contour.Dxf;
using WoodCNC.Contour.ImageAssist;
using WoodCNC.Contour.Templates;
using WoodCNC.Core.Contours;
using WoodCNC.Core.GCode;
using WoodCNC.Core.Geometry;
using WoodCNC.Core.Toolpaths;
using WoodCNC.IO;
using WoodCNC.Parsing;
using WoodCNC.Simulation;

namespace WoodCNC.App;

public partial class MainWindow : Window
{
    private readonly GCodeFileService _fileService = new();
    private readonly GCodeParser _parser = new();
    private readonly TimeEstimator _timeEstimator = new();
    private readonly DxfContourImporter _dxfImporter = new();
    private readonly ImageAssistInspector _imageInspector = new();
    private readonly System.Windows.Threading.DispatcherTimer _timer = new();

    private ImportedGCodeFile? _currentFile;
    private GCodeProgram? _program;
    private ContourProfile? _contourProfile;
    private ContourImportResult? _contourImportResult;
    private int _currentIndex = -1;
    private double _zoomFactor = 1;
    private double _renderScale = 1;
    private double _renderOffsetX;
    private double _renderOffsetY;

    public MainWindow()
    {
        InitializeComponent();
        _timer.Interval = TimeSpan.FromMilliseconds(180);
        _timer.Tick += (_, _) => StepPlayback();
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "G-code 或旧文件|*.txt;*.nc;*.tap;*.gcode|所有文件|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        LoadFile(dialog.FileName);
    }

    private void OpenDxfButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "DXF 文件|*.dxf|所有文件|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        LoadDxf(dialog.FileName);
    }

    private void OpenImageButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp|所有文件|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        LoadImageAssist(dialog.FileName);
    }

    private void LoadImageAssist(string path)
    {
        _timer.Stop();
        _program = null;
        _currentFile = null;
        _contourProfile = null;
        _contourImportResult = null;
        _currentIndex = -1;
        _zoomFactor = 1;
        ZoomText.Text = "100%";
        SegmentList.ItemsSource = null;

        var report = _imageInspector.Inspect(path);
        FileText.Text = $"{System.IO.Path.GetFileName(path)} - 图片辅助";
        SummaryText.Text = $"状态: {report.Status}";
        TimeText.Text = "图片只是辅助轮廓来源，不能直接生成 G-code。";
        DiagnosticText.Text = report.Issues.Count == 0
            ? "图片辅助检查完成。"
            : string.Join(Environment.NewLine, report.Issues);

        SetButtonsEnabled(false);
        ZoomOutButton.IsEnabled = false;
        ZoomInButton.IsEnabled = false;
        FitButton.IsEnabled = false;
        DrawImageAssistPreview(path, report);
        CenterPreviewView();
    }

    private void LoadDxf(string path)
    {
        _timer.Stop();
        _program = null;
        _currentFile = null;
        _currentIndex = -1;
        _contourImportResult = null;
        _zoomFactor = 1;
        ZoomText.Text = "100%";
        SegmentList.ItemsSource = null;
        PlayButton.Content = "播放";

        ContourImportResult result;
        try
        {
            result = _dxfImporter.Import(path);
        }
        catch (Exception ex)
        {
            ShowDxfImportError(path, $"DXF 导入异常: {ex.Message}");
            return;
        }

        _contourProfile = result.RecommendedProfile;
        _contourImportResult = result;
        FileText.Text = $"{System.IO.Path.GetFileName(path)} - DXF 轮廓";
        SummaryText.Text = _contourProfile is null
            ? "未识别到可预览轮廓"
            : $"轮廓实体: {result.Profiles.Count}  平面: {result.Plane}  宽: {result.GroupBounds?.Width ?? _contourProfile.Width:F2}  高: {result.GroupBounds?.Height ?? _contourProfile.Height:F2}  加工原点: {FormatOrigin(result.Origin)}";
        TimeText.Text = "当前为 DXF 轮廓来源预览，尚未生成加工 G-code。";

        var messages = new List<string>();
        messages.AddRange(result.Diagnostics);
        if (_contourProfile is not null)
        {
            messages.AddRange(_contourProfile.Issues);
            if (_contourProfile.CanUseForGeneration)
            {
                messages.Add("DXF 轮廓已识别为可用闭合轮廓，等待尺寸确认和后续刀路生成。");
            }
        }

        DiagnosticText.Text = messages.Count == 0 ? "DXF 导入完成。" : string.Join(Environment.NewLine, messages);
        SetButtonsEnabled(false);
        ZoomOutButton.IsEnabled = _contourProfile is not null;
        ZoomInButton.IsEnabled = _contourProfile is not null;
        FitButton.IsEnabled = _contourProfile is not null;
        if (_contourProfile is null)
        {
            DrawImportMessagePreview("DXF 未生成可预览轮廓", DiagnosticText.Text);
        }
        else
        {
            DrawContourImport(result);
        }

        CenterPreviewView();
    }

    private void LoadFile(string path)
    {
        _timer.Stop();
        _currentIndex = -1;
        _zoomFactor = 1;
        _contourProfile = null;
        _contourImportResult = null;
        ZoomText.Text = "100%";
        _currentFile = _fileService.Import(path);
        _program = _parser.Parse(_currentFile.Text, System.IO.Path.GetFileName(path));

        FileText.Text = $"{System.IO.Path.GetFileName(path)} - {_currentFile.Message}";
        SegmentList.ItemsSource = _program.Segments.Select((segment, index) => $"{index + 1:0000} {segment.Kind,-18} {segment.OriginalLine}").ToList();
        DiagnosticText.Text = _program.Diagnostics.Count == 0
            ? "无解析警告。"
            : string.Join(Environment.NewLine, _program.Diagnostics.Select(x => $"{x.LineNumber}: {x.Message} | {x.OriginalLine}"));

        var estimate = _timeEstimator.Estimate(_program.Segments);
        SummaryText.Text = $"段数: {_program.Segments.Count}  运动段: {_program.MotionCount}  未知/提示: {_program.UnknownCount}";
        TimeText.Text = $"快速: {estimate.RapidSeconds:F1}s  切削: {estimate.CuttingSeconds:F1}s  暂停: {estimate.DwellSeconds:F1}s  总计: {estimate.TotalSeconds:F1}s";

        SetButtonsEnabled(true);
        DrawProgram();
        CenterPreviewView();
    }

    private void GenerateTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        _program = null;
        _currentFile = null;
        _contourImportResult = null;
        _currentIndex = -1;
        SegmentList.ItemsSource = null;
        PlayButton.Content = "播放";

        if (!TryReadTemplateInputs(out var width, out var height, out var radius))
        {
            return;
        }

        var kind = TemplateKindCombo.SelectedIndex switch
        {
            1 => ContourTemplateKind.Circle,
            2 => ContourTemplateKind.Ellipse,
            3 => ContourTemplateKind.RoundedRectangle,
            _ => ContourTemplateKind.Rectangle
        };

        if (kind == ContourTemplateKind.Circle)
        {
            height = width;
            TemplateHeightText.Text = width.ToString("F2");
        }

        var generator = new ContourTemplateGenerator();
        _contourProfile = generator.Generate(kind, width, height, radius);
        FileText.Text = $"轮廓模板 - {TemplateKindCombo.Text}";
        SummaryText.Text = $"轮廓宽: {_contourProfile.Width:F2}  高: {_contourProfile.Height:F2}  点数: {_contourProfile.Points.Count}  闭合: {(_contourProfile.IsClosed ? "是" : "否")}";
        TimeText.Text = "当前为轮廓来源预览，尚未生成加工 G-code。";
        DiagnosticText.Text = _contourProfile.Issues.Count == 0
            ? "轮廓模板已生成，等待后续 S03/S04 参数和刀路生成。"
            : string.Join(Environment.NewLine, _contourProfile.Issues);

        SetButtonsEnabled(false);
        ZoomOutButton.IsEnabled = true;
        ZoomInButton.IsEnabled = true;
        FitButton.IsEnabled = true;
        DrawContour();
        CenterPreviewView();
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_program is null)
        {
            return;
        }

        if (_timer.IsEnabled)
        {
            _timer.Stop();
            PlayButton.Content = "继续";
        }
        else
        {
            _timer.Start();
            PlayButton.Content = "暂停";
        }
    }

    private void StepButton_Click(object sender, RoutedEventArgs e)
    {
        StepPlayback();
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        _currentIndex = -1;
        SegmentList.SelectedIndex = -1;
        PlayButton.Content = "播放";
        DrawProgram();
    }

    private void ExportPlainButton_Click(object sender, RoutedEventArgs e)
    {
        Export(false);
    }

    private void ExportXorButton_Click(object sender, RoutedEventArgs e)
    {
        Export(true);
    }

    private void SegmentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _currentIndex = SegmentList.SelectedIndex;
        DrawProgram();
    }

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
    {
        SetZoom(_zoomFactor / 1.25);
    }

    private void ZoomInButton_Click(object sender, RoutedEventArgs e)
    {
        SetZoom(_zoomFactor * 1.25);
    }

    private void FitButton_Click(object sender, RoutedEventArgs e)
    {
        SetZoom(1);
    }

    private void PreviewCanvas_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (_program is null && _contourProfile is null && _contourImportResult is null)
        {
            return;
        }

        SetZoom(e.Delta > 0 ? _zoomFactor * 1.15 : _zoomFactor / 1.15, e.GetPosition(PreviewCanvas));
    }

    private void SetZoom(double value, Point? canvasAnchor = null)
    {
        var hasPreview = (_program is not null && _program.Segments.Count > 0) ||
            (_contourProfile is not null && _contourProfile.Points.Count > 0) ||
            (_contourImportResult is not null && _contourImportResult.Profiles.Count > 0);
        var anchorOnCanvas = canvasAnchor ?? new Point(
            PreviewScrollViewer.HorizontalOffset + PreviewScrollViewer.ViewportWidth / 2,
            PreviewScrollViewer.VerticalOffset + PreviewScrollViewer.ViewportHeight / 2);
        var anchorInViewport = new Point(
            anchorOnCanvas.X - PreviewScrollViewer.HorizontalOffset,
            anchorOnCanvas.Y - PreviewScrollViewer.VerticalOffset);
        var anchorWorld = hasPreview ? CanvasToWorld(anchorOnCanvas) : new Point2D(0, 0);

        _zoomFactor = Math.Clamp(value, 0.2, 8);
        ZoomText.Text = $"{_zoomFactor * 100:F0}%";
        if (_contourImportResult is not null)
        {
            DrawContourImport(_contourImportResult);
        }
        else if (_contourProfile is not null)
        {
            DrawContour();
        }
        else
        {
            DrawProgram();
        }

        if (!hasPreview)
        {
            return;
        }

        PreviewCanvas.UpdateLayout();
        var newAnchorOnCanvas = WorldToCanvas(anchorWorld);
        PreviewScrollViewer.ScrollToHorizontalOffset(newAnchorOnCanvas.X - anchorInViewport.X);
        PreviewScrollViewer.ScrollToVerticalOffset(newAnchorOnCanvas.Y - anchorInViewport.Y);
    }

    private void StepPlayback()
    {
        if (_program is null || _program.Segments.Count == 0)
        {
            return;
        }

        _currentIndex++;
        if (_currentIndex >= _program.Segments.Count)
        {
            _timer.Stop();
            PlayButton.Content = "播放";
            _currentIndex = _program.Segments.Count - 1;
        }

        SegmentList.SelectedIndex = _currentIndex;
        SegmentList.ScrollIntoView(SegmentList.SelectedItem);
        DrawProgram();
    }

    private void Export(bool xor)
    {
        if (_program is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "文本文件|*.txt|所有文件|*.*",
            FileName = xor ? "export-xor.txt" : "export.nc"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (xor)
        {
            _fileService.ExportXor(dialog.FileName, _program.OriginalText);
        }
        else
        {
            _fileService.ExportPlain(dialog.FileName, _program.OriginalText);
        }

        var roundTrip = _fileService.Import(dialog.FileName, forceXor: xor);
        var parsed = _parser.Parse(roundTrip.Text, System.IO.Path.GetFileName(dialog.FileName));
        DiagnosticText.Text = $"导出完成。回读段数: {parsed.Segments.Count}, 运动段: {parsed.MotionCount}, 提示: {parsed.UnknownCount}";
    }

    private void SetButtonsEnabled(bool enabled)
    {
        PlayButton.IsEnabled = enabled;
        StepButton.IsEnabled = enabled;
        ResetButton.IsEnabled = enabled;
        ExportPlainButton.IsEnabled = enabled;
        ExportXorButton.IsEnabled = enabled;
        ZoomOutButton.IsEnabled = enabled;
        ZoomInButton.IsEnabled = enabled;
        FitButton.IsEnabled = enabled;
    }

    private bool TryReadTemplateInputs(out double width, out double height, out double radius)
    {
        width = 0;
        height = 0;
        radius = 0;

        if (!double.TryParse(TemplateWidthText.Text, out width) || width <= 0)
        {
            DiagnosticText.Text = "宽度/直径必须是大于 0 的数字。";
            return false;
        }

        if (!double.TryParse(TemplateHeightText.Text, out height) || height <= 0)
        {
            DiagnosticText.Text = "高度必须是大于 0 的数字。";
            return false;
        }

        if (!double.TryParse(TemplateRadiusText.Text, out radius) || radius < 0)
        {
            DiagnosticText.Text = "圆角半径必须是大于等于 0 的数字。";
            return false;
        }

        return true;
    }

    private void DrawProgram()
    {
        if (PreviewCanvas is null)
        {
            return;
        }

        PreviewCanvas.Children.Clear();
        if (_program is null || _program.Segments.Count == 0)
        {
            return;
        }

        var bounds = ToolpathGeometry.GetBounds(_program.Segments);
        var scale = GetScale(bounds);
        UpdateCanvasSize(bounds, scale);
        var centerX = (bounds.MinX + bounds.MaxX) / 2;
        var centerY = (bounds.MinY + bounds.MaxY) / 2;
        var offsetX = PreviewCanvas.Width / 2 - centerX * scale;
        var offsetY = PreviewCanvas.Height / 2 + centerY * scale;
        _renderScale = scale;
        _renderOffsetX = offsetX;
        _renderOffsetY = offsetY;
        DrawAxes(bounds, scale, offsetX, offsetY);

        for (var i = 0; i < _program.Segments.Count; i++)
        {
            var segment = _program.Segments[i];
            if (!segment.IsMotion)
            {
                continue;
            }

            var brush = i == _currentIndex ? Brushes.Crimson :
                segment.Kind == ToolpathSegmentKind.Rapid ? Brushes.SlateGray : Brushes.SeaGreen;
            var thickness = i == _currentIndex ? 2.6 : 1.4;

            if (segment.Kind is ToolpathSegmentKind.ArcClockwise or ToolpathSegmentKind.ArcCounterClockwise && segment.Center is not null && segment.Radius is not null)
            {
                DrawArc(segment, brush, thickness, scale, offsetX, offsetY);
            }
            else
            {
                DrawLine(segment.Start, segment.End, brush, thickness, scale, offsetX, offsetY);
            }
        }
    }

    private void DrawContour()
    {
        if (PreviewCanvas is null)
        {
            return;
        }

        PreviewCanvas.Children.Clear();
        if (_contourProfile is null || _contourProfile.Points.Count == 0)
        {
            return;
        }

        var bounds = _contourProfile.Bounds;
        var scale = GetScale(bounds);
        UpdateCanvasSize(bounds, scale);
        var centerX = (bounds.MinX + bounds.MaxX) / 2;
        var centerY = (bounds.MinY + bounds.MaxY) / 2;
        var offsetX = PreviewCanvas.Width / 2 - centerX * scale;
        var offsetY = PreviewCanvas.Height / 2 + centerY * scale;
        _renderScale = scale;
        _renderOffsetX = offsetX;
        _renderOffsetY = offsetY;

        DrawContourAxes(bounds, scale, offsetX, offsetY);

        for (var i = 1; i < _contourProfile.Points.Count; i++)
        {
            var start = _contourProfile.Points[i - 1];
            var end = _contourProfile.Points[i];
            DrawLine(new Point2D(start.X, start.Y), new Point2D(end.X, end.Y), Brushes.RoyalBlue, 2.2, scale, offsetX, offsetY);
        }

        AddContourPointMarker(_contourProfile.Points[0], scale, offsetX, offsetY);
    }

    private void DrawContourImport(ContourImportResult result)
    {
        if (PreviewCanvas is null)
        {
            return;
        }

        PreviewCanvas.Children.Clear();
        if (result.Profiles.Count == 0)
        {
            return;
        }

        var bounds = GetCombinedBounds(result.Profiles);
        var scale = GetScale(bounds);
        UpdateCanvasSize(bounds, scale);
        var centerX = (bounds.MinX + bounds.MaxX) / 2;
        var centerY = (bounds.MinY + bounds.MaxY) / 2;
        var offsetX = PreviewCanvas.Width / 2 - centerX * scale;
        var offsetY = PreviewCanvas.Height / 2 + centerY * scale;
        _renderScale = scale;
        _renderOffsetX = offsetX;
        _renderOffsetY = offsetY;

        DrawContourAxes(bounds, scale, offsetX, offsetY);

        for (var profileIndex = 0; profileIndex < result.Profiles.Count; profileIndex++)
        {
            var profile = result.Profiles[profileIndex];
            var brush = Brushes.RoyalBlue;
            var thickness = 2.2;

            for (var pointIndex = 1; pointIndex < profile.Points.Count; pointIndex++)
            {
                var start = profile.Points[pointIndex - 1];
                var end = profile.Points[pointIndex];
                DrawLine(new Point2D(start.X, start.Y), new Point2D(end.X, end.Y), brush, thickness, scale, offsetX, offsetY);
            }
        }

        if (result.Origin is ContourOrigin origin)
        {
            AddWorkOriginMarker(origin, scale, offsetX, offsetY);
        }
    }

    private void DrawImageAssistPreview(string path, ImageSuitabilityReport report)
    {
        PreviewCanvas.Children.Clear();
        PreviewCanvas.Width = 760;
        PreviewCanvas.Height = 540;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();

            var scale = Math.Min(640d / Math.Max(image.PixelWidth, 1), 400d / Math.Max(image.PixelHeight, 1));
            scale = Math.Min(scale, 1);
            var width = image.PixelWidth * scale;
            var height = image.PixelHeight * scale;
            var left = (PreviewCanvas.Width - width) / 2;
            var top = 74d;

            var frame = new Rectangle
            {
                Width = width + 16,
                Height = height + 16,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.FromRgb(70, 84, 96)),
                StrokeThickness = 1.2
            };
            Canvas.SetLeft(frame, left - 8);
            Canvas.SetTop(frame, top - 8);
            PreviewCanvas.Children.Add(frame);

            var preview = new Image
            {
                Source = image,
                Width = width,
                Height = height,
                Stretch = Stretch.Uniform
            };
            Canvas.SetLeft(preview, left);
            Canvas.SetTop(preview, top);
            PreviewCanvas.Children.Add(preview);

            AddCanvasNotice($"图片已导入，仅做轮廓辅助判断。状态: {report.Status}", 24, 24);
        }
        catch (Exception ex)
        {
            AddCanvasNotice($"图片预览失败: {ex.Message}", 24, 24);
        }
    }

    private void ShowDxfImportError(string path, string message)
    {
        _contourProfile = null;
        FileText.Text = $"{System.IO.Path.GetFileName(path)} - DXF 导入失败";
        SummaryText.Text = "未生成可预览轮廓";
        TimeText.Text = "请根据诊断信息处理文件后重新导入。";
        DiagnosticText.Text = message;
        SetButtonsEnabled(false);
        ZoomOutButton.IsEnabled = false;
        ZoomInButton.IsEnabled = false;
        FitButton.IsEnabled = false;
        DrawImportMessagePreview("DXF 导入失败", message);
        CenterPreviewView();
    }

    private void DrawImportMessagePreview(string title, string message)
    {
        PreviewCanvas.Children.Clear();
        PreviewCanvas.Width = 760;
        PreviewCanvas.Height = 540;
        AddCanvasNotice($"{title}\n{message}", 24, 24);
    }

    private double GetScale(Bounds2D bounds)
    {
        var width = Math.Max(bounds.Width, 1);
        var height = Math.Max(bounds.Height, 1);
        var viewportWidth = PreviewScrollViewer?.ViewportWidth > 0 ? PreviewScrollViewer.ViewportWidth : 760;
        var viewportHeight = PreviewScrollViewer?.ViewportHeight > 0 ? PreviewScrollViewer.ViewportHeight : 540;
        var scaleX = (viewportWidth - 80) / width;
        var scaleY = (viewportHeight - 80) / height;
        return Math.Max(Math.Min(scaleX, scaleY), 0.1) * _zoomFactor;
    }

    private double GetScale(ContourBounds bounds)
    {
        var width = Math.Max(bounds.Width, 1);
        var height = Math.Max(bounds.Height, 1);
        var viewportWidth = PreviewScrollViewer?.ViewportWidth > 0 ? PreviewScrollViewer.ViewportWidth : 760;
        var viewportHeight = PreviewScrollViewer?.ViewportHeight > 0 ? PreviewScrollViewer.ViewportHeight : 540;
        var scaleX = (viewportWidth - 80) / width;
        var scaleY = (viewportHeight - 80) / height;
        return Math.Max(Math.Min(scaleX, scaleY), 0.1) * _zoomFactor;
    }

    private static ContourBounds GetCombinedBounds(IReadOnlyList<ContourProfile> profiles)
    {
        var points = profiles.SelectMany(profile => profile.Points);
        return ContourBounds.FromPoints(points);
    }

    private void UpdateCanvasSize(Bounds2D bounds, double scale)
    {
        var desiredWidth = Math.Max(760, bounds.Width * scale + 120);
        var desiredHeight = Math.Max(540, bounds.Height * scale + 120);
        PreviewCanvas.Width = desiredWidth;
        PreviewCanvas.Height = desiredHeight;
    }

    private void UpdateCanvasSize(ContourBounds bounds, double scale)
    {
        var desiredWidth = Math.Max(760, bounds.Width * scale + 120);
        var desiredHeight = Math.Max(540, bounds.Height * scale + 120);
        PreviewCanvas.Width = desiredWidth;
        PreviewCanvas.Height = desiredHeight;
    }

    private void DrawLine(Point2D start, Point2D end, Brush brush, double thickness, double scale, double offsetX, double offsetY)
    {
        PreviewCanvas.Children.Add(new Line
        {
            X1 = start.X * scale + offsetX,
            Y1 = offsetY - start.Y * scale,
            X2 = end.X * scale + offsetX,
            Y2 = offsetY - end.Y * scale,
            Stroke = brush,
            StrokeThickness = thickness
        });
    }

    private void AddContourPointMarker(ContourPoint point, double scale, double offsetX, double offsetY)
    {
        var x = point.X * scale + offsetX;
        var y = offsetY - point.Y * scale;
        var marker = new Ellipse
        {
            Width = 9,
            Height = 9,
            Fill = Brushes.RoyalBlue,
            Stroke = Brushes.White,
            StrokeThickness = 1.5
        };
        Canvas.SetLeft(marker, x - 4.5);
        Canvas.SetTop(marker, y - 4.5);
        PreviewCanvas.Children.Add(marker);
    }

    private void AddWorkOriginMarker(ContourOrigin origin, double scale, double offsetX, double offsetY)
    {
        var x = origin.X * scale + offsetX;
        var y = offsetY - origin.Y * scale;
        var marker = new Ellipse
        {
            Width = 11,
            Height = 11,
            Fill = Brushes.Red,
            Stroke = Brushes.White,
            StrokeThickness = 1.5
        };
        Canvas.SetLeft(marker, x - 5.5);
        Canvas.SetTop(marker, y - 5.5);
        PreviewCanvas.Children.Add(marker);
    }

    private static string FormatOrigin(ContourOrigin? origin)
    {
        return origin is ContourOrigin ? "(0, 0)" : "未确定";
    }

    private void AddCanvasNotice(string text, double x, double y)
    {
        var label = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(215, 221, 226)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 7, 10, 7),
            Child = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Color.FromRgb(37, 48, 58)),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 620
            }
        };

        Canvas.SetLeft(label, x);
        Canvas.SetTop(label, y);
        PreviewCanvas.Children.Add(label);
    }

    private void CenterPreviewView()
    {
        PreviewCanvas.UpdateLayout();
        PreviewScrollViewer.UpdateLayout();
        PreviewScrollViewer.ScrollToHorizontalOffset(Math.Max(0, (PreviewCanvas.Width - PreviewScrollViewer.ViewportWidth) / 2));
        PreviewScrollViewer.ScrollToVerticalOffset(Math.Max(0, (PreviewCanvas.Height - PreviewScrollViewer.ViewportHeight) / 2));
    }

    private Point2D CanvasToWorld(Point point)
    {
        if (_renderScale <= 0)
        {
            return new Point2D(0, 0);
        }

        return new Point2D(
            (point.X - _renderOffsetX) / _renderScale,
            (_renderOffsetY - point.Y) / _renderScale);
    }

    private Point WorldToCanvas(Point2D point)
    {
        return new Point(
            point.X * _renderScale + _renderOffsetX,
            _renderOffsetY - point.Y * _renderScale);
    }

    private void DrawAxes(Bounds2D bounds, double scale, double offsetX, double offsetY)
    {
        var minX = Math.Min(bounds.MinX, 0);
        var minY = Math.Min(bounds.MinY, 0);
        var negativeX = Math.Abs(minX);
        var negativeY = Math.Abs(minY);
        var maxX = Math.Max(bounds.MaxX, negativeX * 0.25);
        var maxY = Math.Max(bounds.MaxY, negativeY * 0.25);
        var originX = 0 * scale + offsetX;
        var originY = offsetY - 0 * scale;

        var axisBrush = new SolidColorBrush(Color.FromRgb(174, 182, 188));
        var secondaryBrush = new SolidColorBrush(Color.FromRgb(174, 182, 188));
        var dash = new DoubleCollection { 3, 5 };

        AddGuideLine(minX * scale + offsetX, originY, maxX * scale + offsetX, originY, axisBrush, dash);
        AddGuideLine(originX, offsetY - minY * scale, originX, offsetY - maxY * scale, secondaryBrush, dash);

        AddOriginMarker(originX, originY);
        AddAxisLabel("X", maxX * scale + offsetX + 6, originY - 13, axisBrush);
        AddAxisLabel("Y", originX + 6, offsetY - maxY * scale - 15, secondaryBrush);
    }

    private void DrawContourAxes(ContourBounds bounds, double scale, double offsetX, double offsetY)
    {
        var converted = new Bounds2D(bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY);
        DrawAxes(converted, scale, offsetX, offsetY);
    }

    private void AddGuideLine(double x1, double y1, double x2, double y2, Brush brush, DoubleCollection? dash)
    {
        PreviewCanvas.Children.Add(new Line
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = brush,
            StrokeThickness = 0.8,
            StrokeDashArray = dash
        });
    }

    private void AddOriginMarker(double x, double y)
    {
        PreviewCanvas.Children.Add(new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = Brushes.Red
        });
        Canvas.SetLeft(PreviewCanvas.Children[^1], x - 3.5);
        Canvas.SetTop(PreviewCanvas.Children[^1], y - 3.5);
    }

    private void AddAxisLabel(string text, double x, double y, Brush brush)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = brush,
            FontWeight = FontWeights.Normal,
            FontSize = 10,
            Opacity = 0.7
        };
        Canvas.SetLeft(label, x);
        Canvas.SetTop(label, y);
        PreviewCanvas.Children.Add(label);
    }

    private void DrawArc(ToolpathSegment segment, Brush brush, double thickness, double scale, double offsetX, double offsetY)
    {
        var center = segment.Center!.Value;
        var radius = segment.Radius!.Value * scale;
        if (segment.Start.DistanceToXy(segment.End) <= double.Epsilon)
        {
            var ellipse = new Ellipse
            {
                Width = radius * 2,
                Height = radius * 2,
                Stroke = brush,
                StrokeThickness = thickness
            };
            Canvas.SetLeft(ellipse, center.X * scale + offsetX - radius);
            Canvas.SetTop(ellipse, offsetY - center.Y * scale - radius);
            PreviewCanvas.Children.Add(ellipse);
            return;
        }

        var start = new Point(segment.Start.X * scale + offsetX, offsetY - segment.Start.Y * scale);
        var end = new Point(segment.End.X * scale + offsetX, offsetY - segment.End.Y * scale);
        var largeArc = GetSweepRadians(segment) > Math.PI;

        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = end,
            Size = new Size(radius, radius),
            IsLargeArc = largeArc,
            SweepDirection = segment.Kind == ToolpathSegmentKind.ArcClockwise ? SweepDirection.Clockwise : SweepDirection.Counterclockwise
        });

        PreviewCanvas.Children.Add(new Path
        {
            Stroke = brush,
            StrokeThickness = thickness,
            Data = new PathGeometry(new[] { figure })
        });
    }

    private static double GetSweepRadians(ToolpathSegment segment)
    {
        if (segment.Center is null)
        {
            return 0;
        }

        if (segment.Start.DistanceToXy(segment.End) <= double.Epsilon)
        {
            return Math.PI * 2;
        }

        var center = segment.Center.Value;
        var startAngle = Math.Atan2(segment.Start.Y - center.Y, segment.Start.X - center.X);
        var endAngle = Math.Atan2(segment.End.Y - center.Y, segment.End.X - center.X);
        var sweep = endAngle - startAngle;

        if (segment.Kind == ToolpathSegmentKind.ArcClockwise && sweep > 0)
        {
            sweep -= Math.PI * 2;
        }
        else if (segment.Kind == ToolpathSegmentKind.ArcCounterClockwise && sweep < 0)
        {
            sweep += Math.PI * 2;
        }

        return Math.Abs(sweep);
    }
}

