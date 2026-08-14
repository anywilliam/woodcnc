using WoodCNC.Contour;
using WoodCNC.Contour.Dxf;
using WoodCNC.Contour.ImageAssist;
using WoodCNC.Contour.Templates;
using WoodCNC.Core.Contours;

if (args.Length == 1)
{
    ImportExternalDxf(args[0]);
    return;
}

Assert(ContourModuleMarker.Name == "WoodCNC.Contour", "Contour module marker mismatch.");
ContourBoundsFromPoints();
ContourImportResultRecommendation();
ImageSuitabilityReportShape();
GenerateRectangleTemplate();
GenerateCircleTemplate();
RejectInvalidRoundedRectangle();
ImportRectangleLineDxf();
ImportCircleDxf();
DetectOpenLineDxf();
ImportOpenLwPolylineWithoutClosingEdge();
DetectRotatingWorkpieceOrigin();
ImportClassicPolylineVertexDxf();
ConfirmContourSize();
InspectImageAssistFile();

Console.WriteLine("S02 checks passed.");

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void ImportExternalDxf(string path)
{
    var result = new DxfContourImporter().Import(path);
    Console.WriteLine($"Success={result.Success} Profiles={result.Profiles.Count} Recommended={result.RecommendedIndex}");
    foreach (var diagnostic in result.Diagnostics)
    {
        Console.WriteLine($"Diagnostic: {diagnostic}");
    }

    foreach (var profile in result.Profiles.Select((profile, index) => new { profile, index }))
    {
        Console.WriteLine(
            $"Profile {profile.index + 1}: Points={profile.profile.Points.Count} " +
            $"Bounds=({profile.profile.Bounds.MinX:F3},{profile.profile.Bounds.MinY:F3})-({profile.profile.Bounds.MaxX:F3},{profile.profile.Bounds.MaxY:F3}) " +
            $"Closed={profile.profile.IsClosed} Issues={profile.profile.Issues.Count}");
    }
}

static void ContourBoundsFromPoints()
{
    var points = new[]
    {
        new ContourPoint(10, 5),
        new ContourPoint(-2, 8),
        new ContourPoint(4, -3)
    };

    var bounds = ContourBounds.FromPoints(points);
    Assert(Math.Abs(bounds.MinX + 2) < 0.001, "Unexpected MinX.");
    Assert(Math.Abs(bounds.MaxX - 10) < 0.001, "Unexpected MaxX.");
    Assert(Math.Abs(bounds.MinY + 3) < 0.001, "Unexpected MinY.");
    Assert(Math.Abs(bounds.MaxY - 8) < 0.001, "Unexpected MaxY.");
}

static void ContourImportResultRecommendation()
{
    var profile = new ContourProfile
    {
        SourceType = ContourSourceType.ImageAssist,
        SourceFile = "sample.png",
        Points = new[]
        {
            new ContourPoint(0, 0),
            new ContourPoint(10, 0),
            new ContourPoint(10, 10),
            new ContourPoint(0, 0)
        },
        Bounds = new ContourBounds(0, 0, 10, 10),
        IsClosed = true
    };

    var result = new ContourImportResult
    {
        Success = true,
        Profiles = new[] { profile },
        RecommendedIndex = 0
    };

    Assert(result.RecommendedProfile == profile, "Expected recommended profile.");
    Assert(!profile.CanUseForGeneration, "Unconfirmed image assist profile must not be usable.");
}

static void ImageSuitabilityReportShape()
{
    var report = new ImageSuitabilityReport
    {
        Status = ImageSuitabilityStatus.NeedsAdjustment,
        ImageWidth = 800,
        ImageHeight = 600,
        SubjectWidthRatio = 0.25,
        SubjectHeightRatio = 0.7,
        Issues = new[] { "主体宽度占比偏小" }
    };

    Assert(report.Status == ImageSuitabilityStatus.NeedsAdjustment, "Unexpected suitability status.");
    Assert(report.Issues.Count == 1, "Expected one suitability issue.");
}

static void GenerateRectangleTemplate()
{
    var generator = new ContourTemplateGenerator();
    var profile = generator.Generate(ContourTemplateKind.Rectangle, 80, 40);
    Assert(profile.SourceType == ContourSourceType.Template, "Expected template source.");
    Assert(profile.IsClosed, "Expected closed rectangle.");
    Assert(Math.Abs(profile.Width - 80) < 0.001, "Expected rectangle width.");
    Assert(Math.Abs(profile.Height - 40) < 0.001, "Expected rectangle height.");
    Assert(profile.CanUseForGeneration, "Expected rectangle to be usable.");
}

static void GenerateCircleTemplate()
{
    var generator = new ContourTemplateGenerator();
    var profile = generator.Generate(ContourTemplateKind.Circle, 60, 60);
    Assert(profile.IsClosed, "Expected closed circle.");
    Assert(profile.Points.Count > 16, "Expected sampled circle points.");
    Assert(Math.Abs(profile.Width - 60) < 0.001, "Expected circle width.");
    Assert(Math.Abs(profile.Height - 60) < 0.001, "Expected circle height.");
}

static void RejectInvalidRoundedRectangle()
{
    var generator = new ContourTemplateGenerator();
    var profile = generator.Generate(ContourTemplateKind.RoundedRectangle, 40, 20, 20);
    Assert(!profile.CanUseForGeneration, "Expected invalid rounded rectangle to be blocked.");
    Assert(profile.Issues.Count > 0, "Expected rounded rectangle issue.");
}

static void ImportRectangleLineDxf()
{
    var path = WriteTempDxf("""
0
SECTION
0
ENTITIES
0
LINE
10
0
20
0
11
20
21
0
0
LINE
10
20
20
0
11
20
21
10
0
LINE
10
20
20
10
11
0
21
10
0
LINE
10
0
20
10
11
0
21
0
0
ENDSEC
0
EOF
""");

    var result = new DxfContourImporter().Import(path);
    Assert(result.Success, "Expected rectangle DXF import success.");
    var profile = RequireProfile(result, "Expected recommended rectangle DXF profile.");
    Assert(profile.IsClosed, "Expected closed rectangle DXF.");
    Assert(Math.Abs(profile.Width - 20) < 0.001, "Expected DXF rectangle width.");
    Assert(Math.Abs(profile.Height - 10) < 0.001, "Expected DXF rectangle height.");
}

static void ImportCircleDxf()
{
    var path = WriteTempDxf("""
0
SECTION
0
ENTITIES
0
CIRCLE
10
0
20
0
40
15
0
ENDSEC
0
EOF
""");

    var result = new DxfContourImporter().Import(path);
    Assert(result.Success, "Expected circle DXF import success.");
    var profile = RequireProfile(result, "Expected recommended circle DXF profile.");
    Assert(profile.IsClosed, "Expected closed circle DXF.");
    Assert(Math.Abs(profile.Width - 30) < 0.001, "Expected DXF circle width.");
}

static void DetectOpenLineDxf()
{
    var path = WriteTempDxf("""
0
SECTION
0
ENTITIES
0
LINE
10
0
20
0
11
20
21
0
0
ENDSEC
0
EOF
""");

    var result = new DxfContourImporter().Import(path);
    Assert(result.Success, "Expected open line DXF to import for preview.");
    var profile = RequireProfile(result, "Expected recommended open line DXF profile.");
    Assert(!profile.IsClosed, "Expected open line DXF to be marked open.");
    Assert(profile.Issues.Count == 0, "Expected open line DXF to be accepted for rotating workpiece preview.");
}

static void ImportOpenLwPolylineWithoutClosingEdge()
{
    var path = WriteTempDxf("""
0
SECTION
0
ENTITIES
0
LWPOLYLINE
90
4
70
0
10
0
20
10
10
20
20
10
10
20
20
0
10
0
20
0
0
ENDSEC
0
EOF
""");

    var result = new DxfContourImporter().Import(path);
    Assert(result.Success, "Expected open LWPOLYLINE DXF import success.");
    var profile = RequireProfile(result, "Expected open LWPOLYLINE profile.");
    Assert(!profile.IsClosed, "Expected LWPOLYLINE with flag 0 to remain open.");
    Assert(profile.Points.Count == 4, "Expected no synthetic closing point for open LWPOLYLINE.");
}

static void DetectRotatingWorkpieceOrigin()
{
    var path = WriteTempDxf("""
0
SECTION
0
ENTITIES
0
ARC
10
10
20
0
40
5
50
270
51
90
0
LWPOLYLINE
90
3
70
0
10
10
20
5
10
0
20
5
10
0
20
-5
0
ENDSEC
0
EOF
""");

    var result = new DxfContourImporter().Import(path);
    Assert(result.Success, "Expected rotating workpiece contour import success.");
    Assert(result.Plane == ContourPlane.XY, "Expected XY plane.");
    Assert(result.WorkpieceKind == ContourWorkpieceKind.RotatingWorkpieceTopView, "Expected rotating workpiece top view.");
    Assert(result.CanUseForWorkpieceGeneration, "Expected contour group to be usable for workpiece generation.");
    Assert(result.Origin is ContourOrigin, "Expected detected right-end origin.");
    Assert(Math.Abs(result.Origin!.Value.X - 15) < 0.01, "Expected origin on right protruding end.");
    Assert(Math.Abs(result.Origin.Value.Y) < 0.01, "Expected origin on center line.");
}

static void ImportClassicPolylineVertexDxf()
{
    var path = WriteTempDxf("""
0
SECTION
2
ENTITIES
0
POLYLINE
66
1
70
1
0
VERTEX
10
10
20
5
0
VERTEX
10
0
20
5
0
VERTEX
10
0
20
-5
0
VERTEX
10
10
20
-5
0
SEQEND
0
ENDSEC
0
EOF
""");

    var result = new DxfContourImporter().Import(path);
    Assert(result.Success, "Expected classic POLYLINE/VERTEX DXF import success.");
    var profile = RequireProfile(result, "Expected classic POLYLINE profile.");
    Assert(profile.Points.Count == 5, "Expected VERTEX points plus closing point.");
    Assert(profile.IsClosed, "Expected classic POLYLINE flag 1 to close profile.");
    Assert(Math.Abs(profile.Width - 10) < 0.001, "Expected classic POLYLINE width.");
    Assert(Math.Abs(profile.Height - 10) < 0.001, "Expected classic POLYLINE height.");
}

static string WriteTempDxf(string text)
{
    var path = Path.Combine(Path.GetTempPath(), $"woodcnc-{Guid.NewGuid():N}.dxf");
    File.WriteAllText(path, text.Replace("\r\n", "\n"));
    return path;
}

static ContourProfile RequireProfile(ContourImportResult result, string message)
{
    return result.RecommendedProfile ?? throw new InvalidOperationException(message);
}

static void ConfirmContourSize()
{
    var generator = new ContourTemplateGenerator();
    var profile = generator.Generate(ContourTemplateKind.Rectangle, 20, 10);
    var scaled = new ContourScaler().ConfirmSize(profile, 40, 30);
    Assert(scaled.IsConfirmed, "Expected scaled contour to be confirmed.");
    Assert(Math.Abs(scaled.Width - 40) < 0.001, "Expected confirmed width.");
    Assert(Math.Abs(scaled.Height - 30) < 0.001, "Expected confirmed height.");
}

static void InspectImageAssistFile()
{
    var path = Path.Combine(Path.GetTempPath(), $"woodcnc-{Guid.NewGuid():N}.png");
    File.WriteAllBytes(path, new byte[2048]);
    var report = new ImageAssistInspector().Inspect(path);
    Assert(report.Status == ImageSuitabilityStatus.Failed, "Expected invalid image assist file to fail.");
    Assert(report.Issues.Count > 0, "Expected image assist guidance.");
}
