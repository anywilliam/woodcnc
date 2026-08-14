using WoodCNC.Core.Simulation;
using WoodCNC.Core.Toolpaths;

namespace WoodCNC.Simulation;

public sealed class TimeEstimator
{
    public TimeEstimate Estimate(IEnumerable<ToolpathSegment> segments, double rapidFeedPerMinute = 3000, double defaultCutFeedPerMinute = 500)
    {
        var rapidSeconds = 0d;
        var cuttingSeconds = 0d;
        var dwellSeconds = 0d;
        var currentFeed = defaultCutFeedPerMinute;

        foreach (var segment in segments)
        {
            if (segment.FeedRate is > 0)
            {
                currentFeed = segment.FeedRate.Value;
            }

            switch (segment.Kind)
            {
                case ToolpathSegmentKind.Rapid:
                    rapidSeconds += ToSeconds(ToolpathGeometry.GetLength(segment), rapidFeedPerMinute);
                    break;
                case ToolpathSegmentKind.Linear:
                case ToolpathSegmentKind.ArcClockwise:
                case ToolpathSegmentKind.ArcCounterClockwise:
                    cuttingSeconds += ToSeconds(ToolpathGeometry.GetLength(segment), currentFeed);
                    break;
                case ToolpathSegmentKind.Dwell:
                    dwellSeconds += segment.DwellSeconds ?? 0;
                    break;
            }
        }

        return new TimeEstimate(rapidSeconds, cuttingSeconds, dwellSeconds);
    }

    private static double ToSeconds(double length, double feedPerMinute)
    {
        if (feedPerMinute <= 0)
        {
            return 0;
        }

        return length / feedPerMinute * 60;
    }
}

