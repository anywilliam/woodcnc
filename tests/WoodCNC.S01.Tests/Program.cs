using WoodCNC.IO;
using WoodCNC.Parsing;
using WoodCNC.Simulation;

var parser = new GCodeParser();
var estimator = new TimeEstimator();

RoundTripXor();
ParseLineAndArc();
ParseZAxis();
ParseDwellAndUnknown();
ImportSampleIfPresent();

Console.WriteLine("S01 checks passed.");

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

void RoundTripXor()
{
    const string text = "G00 X0 Y0\nG01 X10 Y0 F100\n";
    var bytes = System.Text.Encoding.UTF8.GetBytes(text);
    var encoded = JueCodeCodec.Transform(bytes);
    var decoded = JueCodeCodec.Transform(encoded);
    Assert(System.Text.Encoding.UTF8.GetString(decoded) == text, "XOR round trip failed.");
}

void ParseLineAndArc()
{
    const string text = """
G00 X0 Y0
G01 X10 Y0 F100
G02 X10 Y10 I0 J5
G03 X0 Y10 I-5 J0
""";
    var program = parser.Parse(text, "inline");
    var estimate = estimator.Estimate(program.Segments, rapidFeedPerMinute: 1000, defaultCutFeedPerMinute: 100);
    Assert(program.Segments.Count == 4, "Expected four segments.");
    Assert(program.MotionCount == 4, "Expected four motion segments.");
    Assert(estimate.TotalSeconds > 0, "Expected positive time estimate.");
}

void ParseDwellAndUnknown()
{
    const string text = """
G04 P2
G99 X1
M30
""";
    var program = parser.Parse(text, "inline");
    var estimate = estimator.Estimate(program.Segments);
    Assert(program.Segments.Count == 3, "Expected three command segments.");
    Assert(program.UnknownCount == 1, "Expected one unknown G-code diagnostic.");
    Assert(Math.Abs(estimate.DwellSeconds - 2) < 0.001, "Expected dwell time.");
}

void ParseZAxis()
{
    const string text = """
G00 X0 Y0 Z5
G01 X10 Y0 Z5 F100
G01 X10 Y0 Z-2
G00 X0 Y0 Z5
""";
    var program = parser.Parse(text, "xz");
    Assert(program.Segments.Count == 4, "Expected four three-axis segments.");
    Assert(Math.Abs(program.Segments[0].Start.Z) < 0.001, "Expected initial Z to be zero.");
    Assert(Math.Abs(program.Segments[0].End.Z - 5) < 0.001, "Expected first Z move.");
    Assert(Math.Abs(program.Segments[2].End.Z + 2) < 0.001, "Expected cutting depth.");
    Assert(Math.Abs(ToolpathGeometry.GetLength(program.Segments[2]) - 7) < 0.001, "Expected Z travel length.");
}

void ImportSampleIfPresent()
{
    var root = FindRoot();
    var sample = Path.Combine(root, "40ball.txt");
    if (!File.Exists(sample))
    {
        return;
    }

    var imported = new GCodeFileService().Import(sample);
    var program = parser.Parse(imported.Text, Path.GetFileName(sample));
    Assert(!string.IsNullOrWhiteSpace(imported.Text), "Sample import produced empty text.");
    Assert(program.Segments.Count > 0, "Sample parse produced no segments.");
}

static string FindRoot()
{
    var dir = AppContext.BaseDirectory;
    while (!string.IsNullOrEmpty(dir))
    {
        if (File.Exists(Path.Combine(dir, "TOTAL-PLAN.md")))
        {
            return dir;
        }

        var parent = Directory.GetParent(dir);
        if (parent is null)
        {
            break;
        }

        dir = parent.FullName;
    }

    return Directory.GetCurrentDirectory();
}

