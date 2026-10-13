using TimeTracker.Core;
using Xunit;

namespace TimeTracker.Tests;

public class AiChartMathTests
{
    [Theory]
    [InlineData(0.8)]
    [InlineData(3.7)]
    [InlineData(12.0)]
    [InlineData(47.0)]
    [InlineData(120.0)]
    public void Axis_covers_max_with_nice_step(double max)
    {
        var a = AiChartMath.NiceAxis(max);
        Assert.True(a.Max >= max);
        Assert.True(a.Step > 0);
        Assert.Equal(0, (a.Max / a.Step) % 1, 6);
        var f = a.Step / Math.Pow(10, Math.Floor(Math.Log10(a.Step)));
        Assert.Contains(Math.Round(f, 3), new[] { 1.0, 2.0, 2.5, 5.0 });
    }

    [Fact]
    public void Axis_zero_gives_default()
    {
        var a = AiChartMath.NiceAxis(0);
        Assert.Equal(1, a.Max);
    }

    [Theory]
    [InlineData(0.5, "$0.50")]
    [InlineData(12.34, "$12.3")]
    [InlineData(250.4, "$250")]
    public void Formats_cost(double v, string s) => Assert.Equal(s, AiChartMath.FormatCost(v));

    private static DateTime T(int h, int m = 0) => new(2026, 7, 1, h, m, 0);

    [Fact]
    public void Non_overlapping_sessions_share_lane_zero_with_full_width()
    {
        var r = AiChartMath.AssignLanes(new[] { (T(9), T(10)), (T(10), T(11)), (T(13), T(14)) });
        Assert.All(r, p => { Assert.Equal(0, p.Lane); Assert.Equal(1, p.LaneCount); });
    }

    [Fact]
    public void Overlapping_sessions_get_separate_lanes_in_one_cluster()
    {
        var r = AiChartMath.AssignLanes(new[] { (T(9), T(11)), (T(10), T(12)), (T(10, 30), T(11, 30)) });
        Assert.Equal(new[] { 0, 1, 2 }, r.Select(p => p.Lane).ToArray());
        Assert.All(r, p => Assert.Equal(3, p.LaneCount));
    }

    [Fact]
    public void Lane_is_reused_and_clusters_are_independent()
    {
        // A(9-11) B(10-12) overlap; C(11-13) reuses A's lane; D(15-16) is its own cluster.
        var r = AiChartMath.AssignLanes(new[] { (T(9), T(11)), (T(10), T(12)), (T(11), T(13)), (T(15), T(16)) });
        Assert.Equal(0, r[0].Lane); Assert.Equal(1, r[1].Lane); Assert.Equal(0, r[2].Lane);
        Assert.Equal(2, r[0].LaneCount); Assert.Equal(2, r[2].LaneCount);
        Assert.Equal(1, r[3].LaneCount);
    }

    [Fact]
    public void Input_order_does_not_matter()
    {
        var r = AiChartMath.AssignLanes(new[] { (T(10), T(12)), (T(9), T(11)) });
        Assert.Equal(1, r[0].Lane);
        Assert.Equal(0, r[1].Lane);
    }
}
