using System.Globalization;
using System.IO;
using WoodCNC.Core.Contours;

namespace WoodCNC.Contour.Dxf;

public sealed class DxfContourImporter
{
    public ContourImportResult Import(string path)
    {
        var diagnostics = new List<string>();
        var profiles = new List<ContourProfile>();

        if (!File.Exists(path))
        {
            return new ContourImportResult
            {
                Success = false,
                Profiles = Array.Empty<ContourProfile>(),
                Diagnostics = new[] { "DXF 文件不存在。" }
            };
        }

        string[] lines;
        try
        {
            lines = ReadAllLinesShared(path);
        }
        catch (IOException ex)
        {
            return new ContourImportResult
            {
                Success = false,
                Profiles = Array.Empty<ContourProfile>(),
                Diagnostics = new[] { $"DXF 文件读取失败: {ex.Message}" }
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            return new ContourImportResult
            {
                Success = false,
                Profiles = Array.Empty<ContourProfile>(),
                Diagnostics = new[] { $"DXF 文件没有读取权限: {ex.Message}" }
            };
        }

        try
        {
            var entities = ParseEntities(lines, diagnostics);
            profiles.AddRange(CreateLineProfiles(path, entities.Where(x => x.Type == "LINE").ToList()));

            foreach (var entity in entities.Where(x => x.Type != "LINE"))
            {
                var profile = entity.ToProfile(path);
                if (profile is not null)
                {
                    profiles.Add(profile);
                }
            }

            profiles = MergeConnectedProfiles(profiles).ToList();
        }
        catch (FormatException ex)
        {
            diagnostics.Add($"DXF 数值格式无法解析: {ex.Message}");
        }
        catch (OverflowException ex)
        {
            diagnostics.Add($"DXF 数值超出范围: {ex.Message}");
        }

        int? recommendedIndex = ChooseRecommendedProfile(profiles);
        var groupBounds = profiles.Count > 0 ? ContourBounds.FromPoints(profiles.SelectMany(x => x.Points)) : (ContourBounds?)null;
        var origin = groupBounds is ContourBounds bounds
            ? new ContourOrigin(bounds.MaxX, (bounds.MinY + bounds.MaxY) / 2, "右端中心原点：按导入的 XY 俯视轮廓组最右侧和上下中心推断。")
            : (ContourOrigin?)null;
        if (profiles.Count == 0)
        {
            diagnostics.Add("未识别到可用轮廓。");
        }
        else
        {
            diagnostics.Add("DXF 按旋转工件 XY 俯视轮廓组导入；开放轮廓允许预览和后续工艺确认。");
            diagnostics.Add("右端中心已作为加工原点，后续加工坐标按 (0,0) 归零。");
        }

        return new ContourImportResult
        {
            Success = profiles.Count > 0,
            Profiles = profiles,
            RecommendedIndex = recommendedIndex,
            GroupBounds = groupBounds,
            Origin = origin,
            Diagnostics = diagnostics
        };
    }

    private static IReadOnlyList<ContourProfile> CreateLineProfiles(string path, IReadOnlyList<DxfEntity> lines)
    {
        if (lines.Count == 0)
        {
            return Array.Empty<ContourProfile>();
        }

        return lines.Select(line =>
        {
            var points = new[]
            {
                new ContourPoint(line.X1, line.Y1),
                new ContourPoint(line.X2, line.Y2)
            };

            return new ContourProfile
            {
                SourceType = ContourSourceType.Dxf,
                SourceFile = path,
                Points = points,
                Bounds = ContourBounds.FromPoints(points),
                IsClosed = false,
                Issues = Array.Empty<string>()
            };
        }).ToArray();
    }

    private static string[] ReadAllLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines.ToArray();
    }

    private static IReadOnlyList<ContourProfile> MergeConnectedProfiles(IReadOnlyList<ContourProfile> profiles)
    {
        var remaining = profiles
            .Where(profile => profile.Points.Count > 0)
            .Select(profile => profile with { Points = profile.Points.ToList() })
            .ToList();
        var merged = new List<ContourProfile>();

        while (remaining.Count > 0)
        {
            var current = remaining[0];
            remaining.RemoveAt(0);
            var changed = true;

            while (changed)
            {
                changed = false;
                for (var index = 0; index < remaining.Count; index++)
                {
                    if (!TryMerge(current, remaining[index], out var next))
                    {
                        continue;
                    }

                    current = next;
                    remaining.RemoveAt(index);
                    changed = true;
                    break;
                }
            }

            merged.Add(NormalizeProfile(current));
        }

        return merged;
    }

    private static bool TryMerge(ContourProfile a, ContourProfile b, out ContourProfile merged)
    {
        var aPoints = a.Points.ToList();
        var bPoints = b.Points.ToList();
        var aStart = aPoints[0];
        var aEnd = aPoints[^1];
        var bStart = bPoints[0];
        var bEnd = bPoints[^1];

        if (Near(aEnd, bStart))
        {
            merged = a with { Points = aPoints.Concat(bPoints.Skip(1)).ToList() };
            return true;
        }

        if (Near(aEnd, bEnd))
        {
            bPoints.Reverse();
            merged = a with { Points = aPoints.Concat(bPoints.Skip(1)).ToList() };
            return true;
        }

        if (Near(aStart, bEnd))
        {
            merged = a with { Points = bPoints.Concat(aPoints.Skip(1)).ToList() };
            return true;
        }

        if (Near(aStart, bStart))
        {
            bPoints.Reverse();
            merged = a with { Points = bPoints.Concat(aPoints.Skip(1)).ToList() };
            return true;
        }

        merged = a;
        return false;
    }

    private static ContourProfile NormalizeProfile(ContourProfile profile)
    {
        var points = profile.Points.ToList();
        var isClosed = points.Count > 2 && Near(points[0], points[^1]);
        if (isClosed)
        {
            points[^1] = points[0];
        }

        return profile with
        {
            Points = points,
            Bounds = ContourBounds.FromPoints(points),
            IsClosed = isClosed,
            Issues = Array.Empty<string>()
        };
    }

    private static int? ChooseRecommendedProfile(IReadOnlyList<ContourProfile> profiles)
    {
        if (profiles.Count == 0)
        {
            return null;
        }

        return profiles
            .Select((profile, index) => new { profile, index })
            .OrderByDescending(x => x.profile.IsClosed)
            .ThenBy(x => x.profile.Issues.Count)
            .ThenByDescending(x => x.profile.Bounds.Width * x.profile.Bounds.Height)
            .ThenByDescending(x => x.profile.Points.Count)
            .First()
            .index;
    }

    private static bool SamePoint(ContourPoint a, ContourPoint b)
    {
        return Math.Abs(a.X - b.X) < 0.001 && Math.Abs(a.Y - b.Y) < 0.001;
    }

    private static bool Near(ContourPoint a, ContourPoint b)
    {
        return Math.Abs(a.X - b.X) <= 0.05 && Math.Abs(a.Y - b.Y) <= 0.05;
    }

    private static List<DxfEntity> ParseEntities(string[] lines, List<string> diagnostics)
    {
        var entities = new List<DxfEntity>();
        var currentType = string.Empty;
        var current = new DxfEntity();
        var pendingVertex = new DxfEntity { Type = "VERTEX" };
        var inEntitiesSection = false;
        var pendingSectionMarker = false;
        var collectingClassicPolyline = false;

        for (var i = 0; i + 1 < lines.Length; i += 2)
        {
            var code = lines[i].Trim();
            var value = lines[i + 1].Trim();

            if (code == "0")
            {
                if (pendingSectionMarker && value.Equals("ENTITIES", StringComparison.OrdinalIgnoreCase))
                {
                    inEntitiesSection = true;
                    pendingSectionMarker = false;
                    currentType = string.Empty;
                    current = new DxfEntity();
                    continue;
                }

                if (value.Equals("SECTION", StringComparison.OrdinalIgnoreCase))
                {
                    pendingSectionMarker = true;
                    inEntitiesSection = false;
                    currentType = string.Empty;
                    current = new DxfEntity();
                    pendingVertex = new DxfEntity { Type = "VERTEX" };
                    collectingClassicPolyline = false;
                    continue;
                }

                if (value.Equals("ENDSEC", StringComparison.OrdinalIgnoreCase))
                {
                    if (inEntitiesSection && !string.IsNullOrWhiteSpace(currentType))
                    {
                        if (collectingClassicPolyline && current.Points.Count > 0)
                        {
                            entities.Add(current with { Type = "POLYLINE" });
                            collectingClassicPolyline = false;
                        }
                        else
                        {
                        entities.Add(current with { Type = currentType });
                        }
                    }

                    pendingSectionMarker = false;
                    inEntitiesSection = false;
                    currentType = string.Empty;
                    current = new DxfEntity();
                    pendingVertex = new DxfEntity { Type = "VERTEX" };
                    continue;
                }

                if (!inEntitiesSection)
                {
                    continue;
                }

                var nextType = value.ToUpperInvariant();
                if (collectingClassicPolyline)
                {
                    if (currentType == "VERTEX")
                    {
                        current.Points.AddRange(pendingVertex.Points);
                        pendingVertex = new DxfEntity { Type = "VERTEX" };
                    }

                    if (nextType == "VERTEX")
                    {
                        currentType = "VERTEX";
                        pendingVertex = new DxfEntity { Type = "VERTEX" };
                        continue;
                    }

                    if (nextType == "SEQEND")
                    {
                        entities.Add(current with { Type = "POLYLINE" });
                        currentType = string.Empty;
                        current = new DxfEntity();
                        pendingVertex = new DxfEntity { Type = "VERTEX" };
                        collectingClassicPolyline = false;
                        continue;
                    }
                }
                else if (!string.IsNullOrWhiteSpace(currentType))
                {
                    entities.Add(current with { Type = currentType });
                }

                currentType = nextType;
                current = new DxfEntity();
                if (currentType == "POLYLINE")
                {
                    collectingClassicPolyline = true;
                }
                continue;
            }

            if (pendingSectionMarker && code == "2")
            {
                inEntitiesSection = value.Equals("ENTITIES", StringComparison.OrdinalIgnoreCase);
                pendingSectionMarker = false;
                continue;
            }

            if (!inEntitiesSection)
            {
                continue;
            }

            switch (currentType)
            {
                case "LINE":
                    current = current.AddLine(code, value);
                    break;
                case "CIRCLE":
                    current = current.AddCircle(code, value);
                    break;
                case "ARC":
                    current = current.AddArc(code, value);
                    break;
                case "LWPOLYLINE":
                    current = current.AddPolyline(code, value);
                    break;
                case "POLYLINE":
                    current = current.AddPolylineHeader(code, value);
                    break;
                case "VERTEX":
                    pendingVertex = pendingVertex.AddPolyline(code, value);
                    break;
                default:
                    break;
            }
        }

        if (collectingClassicPolyline)
        {
            if (currentType == "VERTEX")
            {
                current.Points.AddRange(pendingVertex.Points);
            }

            if (current.Points.Count > 0)
            {
                entities.Add(current with { Type = "POLYLINE" });
            }
        }
        else if (!string.IsNullOrWhiteSpace(currentType))
        {
            entities.Add(current with { Type = currentType });
        }

        if (entities.Count == 0)
        {
            diagnostics.Add("DXF 中没有识别到实体。");
        }

        return entities;
    }

    private sealed record DxfEntity
    {
        public string Type { get; init; } = string.Empty;
        public double X1 { get; init; }
        public double Y1 { get; init; }
        public double X2 { get; init; }
        public double Y2 { get; init; }
        public double Radius { get; init; }
        public double StartAngle { get; init; }
        public double EndAngle { get; init; }
        public int Flags { get; init; }
        public List<ContourPoint> Points { get; init; } = new();

        public DxfEntity AddLine(string code, string value) => code switch
        {
            "10" => this with { X1 = Parse(value) },
            "20" => this with { Y1 = Parse(value) },
            "11" => this with { X2 = Parse(value) },
            "21" => this with { Y2 = Parse(value) },
            _ => this
        };

        public DxfEntity AddCircle(string code, string value) => code switch
        {
            "10" => this with { X1 = Parse(value) },
            "20" => this with { Y1 = Parse(value) },
            "40" => this with { Radius = Parse(value) },
            _ => this
        };

        public DxfEntity AddArc(string code, string value) => code switch
        {
            "10" => this with { X1 = Parse(value) },
            "20" => this with { Y1 = Parse(value) },
            "40" => this with { Radius = Parse(value) },
            "50" => this with { StartAngle = Parse(value) },
            "51" => this with { EndAngle = Parse(value) },
            _ => this
        };

        public DxfEntity AddPolyline(string code, string value)
        {
            if (code == "10")
            {
                Points.Add(new ContourPoint(Parse(value), 0));
            }
            else if (code == "20" && Points.Count > 0)
            {
                var point = Points[^1];
                Points[^1] = point with { Y = Parse(value) };
            }
            else if (code == "70")
            {
                return this with { Flags = ParseInt(value) };
            }

            return this;
        }

        public DxfEntity AddPolylineHeader(string code, string value)
        {
            return code == "70" ? this with { Flags = ParseInt(value) } : this;
        }

        public ContourProfile? ToProfile(string sourceFile)
        {
            return Type switch
            {
                "CIRCLE" => CreateCircleProfile(sourceFile),
                "ARC" => CreateArcProfile(sourceFile),
                "LWPOLYLINE" or "POLYLINE" => CreatePolylineProfile(sourceFile),
                _ => null
            };
        }

        private ContourProfile? CreateCircleProfile(string sourceFile)
        {
            var points = new List<ContourPoint>();
            const int segments = 96;
            for (var i = 0; i <= segments; i++)
            {
                var angle = Math.PI * 2 * i / segments;
                points.Add(new ContourPoint(X1 + Math.Cos(angle) * Radius, Y1 + Math.Sin(angle) * Radius));
            }

            points.Add(points[0]);
            return new ContourProfile
            {
                SourceType = ContourSourceType.Dxf,
                SourceFile = sourceFile,
                Points = points,
                Bounds = ContourBounds.FromPoints(points),
                IsClosed = true
            };
        }

        private ContourProfile? CreateArcProfile(string sourceFile)
        {
            var points = new List<ContourPoint>();
            const int segments = 48;
            var start = StartAngle * Math.PI / 180;
            var end = EndAngle * Math.PI / 180;
            if (end < start)
            {
                end += Math.PI * 2;
            }

            for (var i = 0; i <= segments; i++)
            {
                var angle = start + (end - start) * i / segments;
                points.Add(new ContourPoint(X1 + Math.Cos(angle) * Radius, Y1 + Math.Sin(angle) * Radius));
            }

            return new ContourProfile
            {
                SourceType = ContourSourceType.Dxf,
                SourceFile = sourceFile,
                Points = points,
                Bounds = ContourBounds.FromPoints(points),
                IsClosed = false
            };
        }

        private ContourProfile? CreatePolylineProfile(string sourceFile)
        {
            var points = Points.ToList();
            var isClosedByFlag = (Flags & 1) == 1;
            if (isClosedByFlag && points.Count > 0 && !SamePoint(points[0], points[^1]))
            {
                points.Add(points[0]);
            }
            var isClosed = points.Count > 2 && SamePoint(points[0], points[^1]);

            return new ContourProfile
            {
                SourceType = ContourSourceType.Dxf,
                SourceFile = sourceFile,
                Points = points,
                Bounds = ContourBounds.FromPoints(points),
                IsClosed = isClosed,
                Issues = Array.Empty<string>()
            };
        }

        private static double Parse(string value) => double.Parse(value, CultureInfo.InvariantCulture);
        private static int ParseInt(string value) => int.Parse(value, CultureInfo.InvariantCulture);
    }
}
