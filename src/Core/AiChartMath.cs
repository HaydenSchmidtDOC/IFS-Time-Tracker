namespace TimeTracker.Core;

public readonly record struct CostAxis(double Max, double Step);

public readonly record struct LanePlacement(int Index, int Lane, int LaneCount);

/// <summary>UI-independent maths for the AI lane: cost axis, calendar overlap lanes, period totals.</summary>
public static class AiChartMath
{
    /// <summary>Axis for 0..<paramref name="maxCost"/> using a 1/2/2.5/5/10 step, aiming for about
    /// <paramref name="targetTicks"/> gridlines. Max is a whole multiple of Step and covers maxCost.</summary>
    public static CostAxis NiceAxis(double maxCost, int targetTicks = 4)
    {
        if (maxCost <= 0 || double.IsNaN(maxCost)) return new CostAxis(1, 0.25);
        var raw = maxCost / Math.Max(1, targetTicks);
        var pow = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var f = raw / pow;
        var nice = f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10;
        var step = nice * pow;
        var max = Math.Ceiling(maxCost / step - 1e-9) * step;
        return new CostAxis(Math.Round(max, 6), Math.Round(step, 6));
    }

    public static string FormatCost(double usd)
        => usd >= 100 ? $"${usd:0}" : usd >= 10 ? $"${usd:0.0}" : $"${usd:0.00}";

    /// <summary>
    /// Calendar-style overlap layout: intervals that touch through a chain of overlaps form a cluster;
    /// each gets the first free lane and the cluster's lane count sets the width divisor. Intervals
    /// that merely end when another starts do not overlap.
    /// </summary>
    public static LanePlacement[] AssignLanes(IReadOnlyList<(DateTime Start, DateTime End)> items)
    {
        var order = Enumerable.Range(0, items.Count).OrderBy(i => items[i].Start).ThenBy(i => items[i].End).ToList();
        var result = new LanePlacement[items.Count];
        var cluster = new List<int>();
        var laneEnds = new List<DateTime>();
        var clusterEnd = DateTime.MinValue;

        void Flush()
        {
            foreach (var i in cluster) result[i] = result[i] with { LaneCount = laneEnds.Count };
            cluster.Clear(); laneEnds.Clear();
        }

        foreach (var i in order)
        {
            var (s, e) = items[i];
            if (e <= s) e = s.AddMinutes(1);
            if (cluster.Count > 0 && s >= clusterEnd) Flush();

            var lane = laneEnds.FindIndex(end => end <= s);
            if (lane < 0) { lane = laneEnds.Count; laneEnds.Add(e); } else laneEnds[lane] = e;

            result[i] = new LanePlacement(i, lane, 0);
            cluster.Add(i);
            clusterEnd = cluster.Count == 1 ? e : (e > clusterEnd ? e : clusterEnd);
        }
        Flush();
        return result;
    }

    /// <summary>Sessions attributed to local date by their start time (not split across midnight).</summary>
    public static DateTime LocalDay(AiSession s) => s.StartedUtc.ToLocalTime().Date;

    public static double TotalFor(IEnumerable<AiSession> sessions, Func<AiSession, bool> where)
        => sessions.Where(where).Sum(s => s.CostUsd);
}
