using System.Globalization;
using System.Text.RegularExpressions;
using WoodCNC.Core.GCode;
using WoodCNC.Core.Geometry;
using WoodCNC.Core.Toolpaths;

namespace WoodCNC.Parsing;

public sealed partial class GCodeParser
{
    public GCodeProgram Parse(string text, string sourceName)
    {
        var segments = new List<ToolpathSegment>();
        var diagnostics = new List<ParseDiagnostic>();
        var current = new Point2D(0, 0);
        double? currentFeed = null;

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var rawLine = lines[index];
            var line = StripComment(rawLine).Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var words = ParseWords(line);
            if (words.TryGetValue('F', out var feed))
            {
                currentFeed = feed;
            }

            if (words.TryGetValue('G', out var g))
            {
                var gCode = (int)Math.Round(g);
                switch (gCode)
                {
                    case 0:
                    case 1:
                    {
                        var end = new Point2D(
                            words.TryGetValue('X', out var x) ? x : current.X,
                            words.TryGetValue('Y', out var y) ? y : current.Y,
                            words.TryGetValue('Z', out var z) ? z : current.Z);
                        segments.Add(new ToolpathSegment
                        {
                            Kind = gCode == 0 ? ToolpathSegmentKind.Rapid : ToolpathSegmentKind.Linear,
                            Start = current,
                            End = end,
                            FeedRate = currentFeed,
                            OriginalLine = rawLine
                        });
                        current = end;
                        break;
                    }
                    case 2:
                    case 3:
                    {
                        var end = new Point2D(
                            words.TryGetValue('X', out var x) ? x : current.X,
                            words.TryGetValue('Y', out var y) ? y : current.Y,
                            words.TryGetValue('Z', out var z) ? z : current.Z);

                        var center = TryGetArcCenter(current, end, words, gCode == 2, out var radius);
                        if (center is null)
                        {
                            diagnostics.Add(new ParseDiagnostic(index + 1, "圆弧缺少 I/J 或 R 参数", rawLine));
                            segments.Add(Command(rawLine, current));
                            break;
                        }

                        segments.Add(new ToolpathSegment
                        {
                            Kind = gCode == 2 ? ToolpathSegmentKind.ArcClockwise : ToolpathSegmentKind.ArcCounterClockwise,
                            Start = current,
                            End = end,
                            Center = center.Value,
                            Radius = radius,
                            FeedRate = currentFeed,
                            OriginalLine = rawLine
                        });
                        current = end;
                        break;
                    }
                    case 4:
                    {
                        var seconds = words.TryGetValue('P', out var p) ? p : 0;
                        segments.Add(new ToolpathSegment
                        {
                            Kind = ToolpathSegmentKind.Dwell,
                            Start = current,
                            End = current,
                            DwellSeconds = seconds,
                            OriginalLine = rawLine
                        });
                        break;
                    }
                    default:
                        diagnostics.Add(new ParseDiagnostic(index + 1, $"暂不支持 G{gCode:00}", rawLine));
                        segments.Add(Command(rawLine, current));
                        break;
                }

                continue;
            }

            if (words.ContainsKey('M'))
            {
                segments.Add(Command(rawLine, current));
                continue;
            }

            diagnostics.Add(new ParseDiagnostic(index + 1, "未识别指令，已保留原始行", rawLine));
            segments.Add(Command(rawLine, current));
        }

        return new GCodeProgram
        {
            SourceName = sourceName,
            OriginalText = text,
            Segments = segments,
            Diagnostics = diagnostics
        };
    }

    private static ToolpathSegment Command(string rawLine, Point2D current)
    {
        return new ToolpathSegment
        {
            Kind = ToolpathSegmentKind.Command,
            Start = current,
            End = current,
            OriginalLine = rawLine
        };
    }

    private static Point2D? TryGetArcCenter(
        Point2D start,
        Point2D end,
        IReadOnlyDictionary<char, double> words,
        bool clockwise,
        out double? radius)
    {
        var hasI = words.TryGetValue('I', out var i);
        var hasJ = words.TryGetValue('J', out var j);
        if (hasI || hasJ)
        {
            var center = new Point2D(start.X + i, start.Y + j, start.Z);
            radius = start.DistanceToXy(center);
            return center;
        }

        if (!words.TryGetValue('R', out var r) || Math.Abs(r) < double.Epsilon)
        {
            radius = null;
            return null;
        }

        radius = Math.Abs(r);
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var chord = Math.Sqrt(dx * dx + dy * dy);
        if (chord <= double.Epsilon || chord > 2 * radius)
        {
            return null;
        }

        var midpoint = new Point2D((start.X + end.X) / 2, (start.Y + end.Y) / 2);
        var height = Math.Sqrt(radius.Value * radius.Value - chord * chord / 4);
        var nx = -dy / chord;
        var ny = dx / chord;
        var sign = clockwise ? -1 : 1;
        if (r < 0)
        {
            sign *= -1;
        }

        return new Point2D(midpoint.X + sign * height * nx, midpoint.Y + sign * height * ny, start.Z);
    }

    private static Dictionary<char, double> ParseWords(string line)
    {
        var result = new Dictionary<char, double>();
        foreach (Match match in WordRegex().Matches(line))
        {
            var letter = char.ToUpperInvariant(match.Groups[1].Value[0]);
            if (double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                result[letter] = value;
            }
        }

        return result;
    }

    private static string StripComment(string line)
    {
        var semicolon = line.IndexOf(';');
        if (semicolon >= 0)
        {
            line = line[..semicolon];
        }

        return ParenthesesCommentRegex().Replace(line, string.Empty);
    }

    [GeneratedRegex(@"([A-Za-z])\s*([-+]?\d+(?:\.\d+)?)")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"\([^)]*\)")]
    private static partial Regex ParenthesesCommentRegex();
}

