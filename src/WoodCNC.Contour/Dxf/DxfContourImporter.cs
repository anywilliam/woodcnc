using System.Globalization;
using System.IO;
using WoodCNC.Core.Contours;

namespace WoodCNC.Contour.Dxf;

public sealed class DxfContourImporter
{
    private const double MinimumEndpointTolerance = 0.001;
    private const double MaximumEndpointTolerance = 0.05;

    public ContourImportResult Import(string path)
    {
        var diagnostics = new List<string>();

        if (!File.Exists(path))
        {
            return Failed("DXF 文件不存在。");
        }

        string[] lines;
        try
        {
            lines = ReadAllLinesShared(path);
        }
        catch (IOException ex)
        {
            return Failed($"DXF 文件读取失败: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Failed($"DXF 文件没有读取权限: {ex.Message}");
        }

        IReadOnlyList<DxfEntity> entities;
        try
        {
            entities = ParseEntities(lines, diagnostics);
        }
        catch (FormatException ex)
        {
            diagnostics.Add($"DXF 数值格式无法解析: {ex.Message}");
            entities = Array.Empty<DxfEntity>();
        }
        catch (OverflowException ex)
        {
            diagnostics.Add($"DXF 数值超出范围: {ex.Message}");
            entities = Array.Empty<DxfEntity>();
        }

        var geometry = BuildGeometry(path, entities, diagnostics);
        var profiles = BuildProfilesFromGeometry(path, geometry, diagnostics).ToList();

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

        static ContourImportResult Failed(string diagnostic) => new()
        {
            Success = false,
            Profiles = Array.Empty<ContourProfile>(),
            Diagnostics = new[] { diagnostic }
        };
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

    private static GeometryBuildResult BuildGeometry(string path, IReadOnlyList<DxfEntity> entities, List<string> diagnostics)
    {
        var segments = new List<ContourSegment>();
        var closedProfiles = new List<ContourProfile>();
        var unsupported = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var entity in entities.Select((entity, index) => new { entity, index }))
        {
            switch (entity.entity.Type)
            {
                case "LINE":
                    segments.Add(CreateLineSegment(entity.entity, entity.index));
                    break;
                case "ARC":
                    segments.Add(CreateArcSegment(entity.entity, entity.index));
                    break;
                case "CIRCLE":
                    closedProfiles.Add(entity.entity.CreateCircleProfile(path, entity.index));
                    break;
                case "LWPOLYLINE":
                case "POLYLINE":
                    segments.AddRange(CreatePolylineSegments(entity.entity, entity.index));
                    break;
                default:
                    unsupported.TryGetValue(entity.entity.Type, out var count);
                    unsupported[entity.entity.Type] = count + 1;
                    break;
            }
        }

        foreach (var item in unsupported.OrderBy(x => x.Key))
        {
            diagnostics.Add($"DXF 暂不支持实体 {item.Key}，数量 {item.Value}。");
        }

        diagnostics.Add($"DXF 实体 {entities.Count} 个，几何段 {segments.Count} 条，独立闭合轮廓 {closedProfiles.Count} 条。");
        return new GeometryBuildResult(segments, closedProfiles);
    }

    private static IEnumerable<ContourProfile> BuildProfilesFromGeometry(string path, GeometryBuildResult geometry, List<string> diagnostics)
    {
        foreach (var profile in geometry.ClosedProfiles)
        {
            yield return profile;
        }

        foreach (var chain in BuildChains(geometry.Segments, diagnostics))
        {
            if (chain.Points.Count < 2)
            {
                continue;
            }

            yield return new ContourProfile
            {
                SourceType = ContourSourceType.Dxf,
                SourceFile = path,
                Points = chain.Points,
                Bounds = ContourBounds.FromPoints(chain.Points),
                IsClosed = chain.IsClosed,
                Issues = chain.Issues
            };
        }
    }

    private static IReadOnlyList<ContourChain> BuildChains(IReadOnlyList<ContourSegment> segments, List<string> diagnostics)
    {
        if (segments.Count == 0)
        {
            return Array.Empty<ContourChain>();
        }

        var tolerance = CalculateEndpointTolerance(segments);
        var clusters = EndpointClusters.Build(segments, tolerance);
        var segmentStates = segments
            .Select((segment, index) => new SegmentState(index, segment, clusters.IdOf(segment.Start), clusters.IdOf(segment.End)))
            .ToArray();
        var byCluster = segmentStates
            .SelectMany(segment => new[]
            {
                new ClusterSegment(segment.StartCluster, segment.Index),
                new ClusterSegment(segment.EndCluster, segment.Index)
            })
            .GroupBy(x => x.ClusterId)
            .ToDictionary(x => x.Key, x => x.Select(item => item.SegmentIndex).Distinct().ToList());
        var unused = new HashSet<int>(segmentStates.Select(x => x.Index));
        var chains = new List<ContourChain>();
        var ambiguousClusters = byCluster.Where(x => x.Value.Count > 2).Select(x => x.Key).ToArray();

        if (ambiguousClusters.Length > 0)
        {
            diagnostics.Add($"DXF 存在 {ambiguousClusters.Length} 个分叉/歧义连接点，相关轮廓会标记问题并阻断生成。");
        }

        while (unused.Count > 0)
        {
            var seed = ChooseSeed(unused, segmentStates, byCluster);
            unused.Remove(seed.Index);

            var oriented = OrientSeed(seed, byCluster);
            var points = oriented.Segment.Points.ToList();
            var usedClusters = new HashSet<int> { oriented.StartCluster, oriented.EndCluster };
            var issues = new List<string>();
            var currentCluster = oriented.EndCluster;
            var startCluster = oriented.StartCluster;

            while (true)
            {
                var nextCandidates = byCluster.TryGetValue(currentCluster, out var attached)
                    ? attached.Where(unused.Contains).ToList()
                    : new List<int>();

                if (nextCandidates.Count == 0)
                {
                    break;
                }

                if (nextCandidates.Count > 1)
                {
                    issues.Add("轮廓连接存在歧义，需要人工确认。");
                    break;
                }

                var next = segmentStates[nextCandidates[0]];
                unused.Remove(next.Index);
                var nextOriented = OrientFromCluster(next, currentCluster);
                AppendPoints(points, nextOriented.Segment.Points);
                currentCluster = nextOriented.EndCluster;
                usedClusters.Add(currentCluster);

                if (currentCluster == startCluster)
                {
                    break;
                }
            }

            var isClosed = points.Count > 2 && Near(points[0], points[^1], tolerance);
            if (isClosed)
            {
                points[^1] = points[0];
            }
            else
            {
                var startDegree = byCluster.TryGetValue(startCluster, out var startAttached) ? startAttached.Count : 0;
                var endDegree = byCluster.TryGetValue(currentCluster, out var endAttached) ? endAttached.Count : 0;
                if (startDegree != 1 || endDegree != 1)
                {
                    issues.Add("轮廓未闭合且端点连接不完整，需要确认断点。");
                }
            }

            if (usedClusters.Any(ambiguousClusters.Contains) && !issues.Contains("轮廓连接存在歧义，需要人工确认。"))
            {
                issues.Add("轮廓经过分叉连接点，需要人工确认。");
            }

            chains.Add(new ContourChain(points, isClosed, issues.Distinct().ToArray()));
        }

        diagnostics.Add($"DXF 拓扑合并得到 {chains.Count} 条轮廓链，端点容差 {tolerance:F4}。");
        return chains;
    }

    private static SegmentState ChooseSeed(HashSet<int> unused, IReadOnlyList<SegmentState> segments, IReadOnlyDictionary<int, List<int>> byCluster)
    {
        return unused
            .Select(index => segments[index])
            .OrderByDescending(segment => IsEndpoint(segment.StartCluster, byCluster) || IsEndpoint(segment.EndCluster, byCluster))
            .ThenBy(segment => segment.Index)
            .First();
    }

    private static OrientedSegment OrientSeed(SegmentState segment, IReadOnlyDictionary<int, List<int>> byCluster)
    {
        if (IsEndpoint(segment.EndCluster, byCluster) && !IsEndpoint(segment.StartCluster, byCluster))
        {
            return new OrientedSegment(segment.Segment.Reversed(), segment.EndCluster, segment.StartCluster);
        }

        return new OrientedSegment(segment.Segment, segment.StartCluster, segment.EndCluster);
    }

    private static OrientedSegment OrientFromCluster(SegmentState segment, int cluster)
    {
        if (segment.StartCluster == cluster)
        {
            return new OrientedSegment(segment.Segment, segment.StartCluster, segment.EndCluster);
        }

        return new OrientedSegment(segment.Segment.Reversed(), segment.EndCluster, segment.StartCluster);
    }

    private static bool IsEndpoint(int clusterId, IReadOnlyDictionary<int, List<int>> byCluster)
    {
        return byCluster.TryGetValue(clusterId, out var attached) && attached.Count == 1;
    }

    private static void AppendPoints(List<ContourPoint> target, IReadOnlyList<ContourPoint> next)
    {
        if (next.Count == 0)
        {
            return;
        }

        var skip = target.Count > 0 && Near(target[^1], next[0], MaximumEndpointTolerance) ? 1 : 0;
        target.AddRange(next.Skip(skip));
    }

    private static ContourSegment CreateLineSegment(DxfEntity entity, int entityIndex)
    {
        var start = new ContourPoint(entity.X1, entity.Y1);
        var end = new ContourPoint(entity.X2, entity.Y2);
        return new ContourSegment
        {
            Kind = ContourSegmentKind.Line,
            Start = start,
            End = end,
            Points = new[] { start, end },
            SourceEntityType = entity.Type,
            SourceEntityIndex = entityIndex
        };
    }

    private static ContourSegment CreateArcSegment(DxfEntity entity, int entityIndex)
    {
        var points = SampleArc(entity.X1, entity.Y1, entity.Radius, entity.StartAngle, entity.EndAngle, 48);
        return new ContourSegment
        {
            Kind = ContourSegmentKind.Arc,
            Start = points[0],
            End = points[^1],
            Points = points,
            SourceEntityType = entity.Type,
            SourceEntityIndex = entityIndex
        };
    }

    private static IEnumerable<ContourSegment> CreatePolylineSegments(DxfEntity entity, int entityIndex)
    {
        if (entity.Points.Count < 2)
        {
            yield break;
        }

        var points = entity.Points.ToList();
        var isClosedByFlag = (entity.Flags & 1) == 1;
        if (isClosedByFlag && !Near(points[0], points[^1], MinimumEndpointTolerance))
        {
            points.Add(points[0]);
        }

        yield return new ContourSegment
        {
            Kind = ContourSegmentKind.PolylinePart,
            Start = points[0],
            End = points[^1],
            Points = points,
            SourceEntityType = entity.Type,
            SourceEntityIndex = entityIndex
        };
    }

    private static IReadOnlyList<ContourPoint> SampleArc(double centerX, double centerY, double radius, double startAngle, double endAngle, int minimumSegments)
    {
        var points = new List<ContourPoint>();
        var start = startAngle * Math.PI / 180;
        var end = endAngle * Math.PI / 180;
        if (end < start)
        {
            end += Math.PI * 2;
        }

        var sweep = end - start;
        var segments = Math.Max(minimumSegments, (int)Math.Ceiling(sweep / (Math.PI / 48)));
        for (var i = 0; i <= segments; i++)
        {
            var angle = start + sweep * i / segments;
            points.Add(new ContourPoint(centerX + Math.Cos(angle) * radius, centerY + Math.Sin(angle) * radius));
        }

        return points;
    }

    private static double CalculateEndpointTolerance(IReadOnlyList<ContourSegment> segments)
    {
        var points = segments.SelectMany(segment => new[] { segment.Start, segment.End }).ToArray();
        var bounds = ContourBounds.FromPoints(points);
        var diagonal = Math.Sqrt(bounds.Width * bounds.Width + bounds.Height * bounds.Height);
        return Math.Clamp(diagonal * 0.00001, MinimumEndpointTolerance, MaximumEndpointTolerance);
    }

    private static int? ChooseRecommendedProfile(IReadOnlyList<ContourProfile> profiles)
    {
        if (profiles.Count == 0)
        {
            return null;
        }

        return profiles
            .Select((profile, index) => new { profile, index })
            .OrderBy(x => x.profile.Issues.Count)
            .ThenByDescending(x => x.profile.IsClosed)
            .ThenByDescending(x => x.profile.Bounds.Width * x.profile.Bounds.Height)
            .ThenByDescending(x => x.profile.Points.Count)
            .First()
            .index;
    }

    private static bool SamePoint(ContourPoint a, ContourPoint b)
    {
        return Math.Abs(a.X - b.X) < 0.001 && Math.Abs(a.Y - b.Y) < 0.001;
    }

    private static bool Near(ContourPoint a, ContourPoint b, double tolerance)
    {
        return Math.Abs(a.X - b.X) <= tolerance && Math.Abs(a.Y - b.Y) <= tolerance;
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
                current = new DxfEntity { Type = nextType };
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

    private sealed record GeometryBuildResult(IReadOnlyList<ContourSegment> Segments, IReadOnlyList<ContourProfile> ClosedProfiles);
    private sealed record SegmentState(int Index, ContourSegment Segment, int StartCluster, int EndCluster);
    private sealed record ClusterSegment(int ClusterId, int SegmentIndex);
    private sealed record OrientedSegment(ContourSegment Segment, int StartCluster, int EndCluster);
    private sealed record ContourChain(IReadOnlyList<ContourPoint> Points, bool IsClosed, IReadOnlyList<string> Issues);

    private sealed class EndpointClusters
    {
        private readonly List<EndpointCluster> _clusters;
        private readonly Dictionary<ContourPoint, int> _ids;

        private EndpointClusters(List<EndpointCluster> clusters, Dictionary<ContourPoint, int> ids)
        {
            _clusters = clusters;
            _ids = ids;
        }

        public static EndpointClusters Build(IReadOnlyList<ContourSegment> segments, double tolerance)
        {
            var clusters = new List<EndpointCluster>();
            var ids = new Dictionary<ContourPoint, int>();

            foreach (var point in segments.SelectMany(segment => new[] { segment.Start, segment.End }))
            {
                var id = FindOrAdd(clusters, point, tolerance);
                ids[point] = id;
            }

            return new EndpointClusters(clusters, ids);
        }

        public int IdOf(ContourPoint point) => _ids[point];

        private static int FindOrAdd(List<EndpointCluster> clusters, ContourPoint point, double tolerance)
        {
            for (var i = 0; i < clusters.Count; i++)
            {
                if (Near(clusters[i].Center, point, tolerance))
                {
                    clusters[i] = clusters[i].Add(point);
                    return i;
                }
            }

            clusters.Add(new EndpointCluster(point, 1));
            return clusters.Count - 1;
        }
    }

    private sealed record EndpointCluster(ContourPoint Center, int Count)
    {
        public EndpointCluster Add(ContourPoint point)
        {
            var nextCount = Count + 1;
            return new EndpointCluster(
                new ContourPoint((Center.X * Count + point.X) / nextCount, (Center.Y * Count + point.Y) / nextCount),
                nextCount);
        }
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

        public ContourProfile CreateCircleProfile(string sourceFile, int entityIndex)
        {
            var points = SampleArc(X1, Y1, Radius, 0, 360, 96).ToList();
            if (!SamePoint(points[0], points[^1]))
            {
                points.Add(points[0]);
            }

            return new ContourProfile
            {
                SourceType = ContourSourceType.Dxf,
                SourceFile = sourceFile,
                Points = points,
                Bounds = ContourBounds.FromPoints(points),
                IsClosed = true,
                Issues = Array.Empty<string>()
            };
        }

        private static double Parse(string value) => double.Parse(value, CultureInfo.InvariantCulture);
        private static int ParseInt(string value) => int.Parse(value, CultureInfo.InvariantCulture);
    }
}
