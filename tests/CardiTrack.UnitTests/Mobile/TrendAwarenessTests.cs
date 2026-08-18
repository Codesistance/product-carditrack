using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Charts;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// Pins which days the daybook detail's awareness lines count and what the sentence claims —
/// counts a caregiver can check against the chart beside them, never scores, per the release
/// matrix's standing "no risk scores" decision.
/// </summary>
public class TrendAwarenessTests
{
    private static readonly DateOnly Today = new(2026, 8, 18);

    private static List<MetricPoint> Series(params decimal?[] values) =>
        values.Select((value, i) => new MetricPoint
        {
            Date = Today.AddDays(-(values.Length - 1 - i)),
            Value = value,
        }).ToList();

    [Fact]
    public void CountsTheDaysBelowTheUsual()
    {
        // 10 of 14 nights under a 420-minute usual; the four at 430-460 are the other side.
        var series = Series(400, 410, 430, 390, 380, 440, 370, 360, 350, 450, 340, 330, 460, 320);

        var line = TrendAwareness.Line(series, 420, TrendAwareness.Direction.BelowUsual, "night");

        Assert.Equal("Under their usual on 10 of the last 14 nights", line);
    }

    [Fact]
    public void CountsTheDaysAboveTheUsual_ForTheMetricsWhereHighIsTheConcern()
    {
        var series = Series(70, 72, 68, 75, 74, 66, 71);

        var line = TrendAwareness.Line(series, 69, TrendAwareness.Direction.AboveUsual, "day");

        Assert.Equal("Above their usual on 5 of the last 7 days", line);
    }

    /// <summary>No baseline means no claim — a count against an invented usual would be the
    /// fabricated normal this product refuses everywhere else.</summary>
    [Fact]
    public void SaysNothing_WithoutABaseline()
    {
        Assert.Null(TrendAwareness.Line(
            Series(1, 2, 3, 4, 5, 6, 7), null, TrendAwareness.Direction.BelowUsual, "day"));
    }

    /// <summary>
    /// Below the floor the sentence would be counting noise: "under their usual on 2 of the last
    /// 3 nights" reads as a pattern and is a long weekend.
    /// </summary>
    [Fact]
    public void SaysNothing_WithTooFewMeasuredDays()
    {
        var series = Series(100, null, null, 90, null, 80, null, null, null, 70, null, 60, 50, null);

        Assert.Null(TrendAwareness.Line(series, 95, TrendAwareness.Direction.BelowUsual, "day"));
    }

    /// <summary>
    /// A partial day is a running total and would count as "under their usual" every single
    /// morning; an unmeasured day is silence. Neither is a day the sentence may claim.
    /// </summary>
    [Fact]
    public void CountsNeitherPartialNorUnmeasuredDays()
    {
        var series = Series(500, 510, 520, 490, 480, 470, 460, 450, 440);
        series.Add(new MetricPoint { Date = Today, Value = 12, IsPartial = true });

        var line = TrendAwareness.Line(series, 600, TrendAwareness.Direction.BelowUsual, "day");

        // Nine finished days counted; the partial tenth is not on either side of the sentence.
        Assert.Equal("Under their usual on 9 of the last 9 days", line);
    }

    /// <summary>
    /// The band count is the entry page's one inferential claim beyond the member's own baseline,
    /// and the sentence must carry the bound with its unit and its publisher — a count against an
    /// unattributed figure is a claim wearing no authority.
    /// </summary>
    [Fact]
    public void BandLine_CountsAgainstThePublishedBound_AndSaysWhoPublishesIt()
    {
        var series = Series(93.8m, 95.1m, 96.0m, 93.5m, 94.9m, 95.6m, 93.9m);

        var line = TrendAwareness.BandLine(
            series, 94m, TrendAwareness.Direction.BelowUsual, "the recommended 94% (WHO)", "day");

        Assert.Equal("Under the recommended 94% (WHO) on 3 of the last 7 days", line);
    }

    /// <summary>Same floor as the usual-count: too few measured days is noise either way.</summary>
    [Fact]
    public void BandLine_SaysNothing_WithTooFewMeasuredDays()
    {
        Assert.Null(TrendAwareness.BandLine(
            Series(90, null, 91, null, 92, null), 94m,
            TrendAwareness.Direction.BelowUsual, "the recommended 94% (WHO)", "day"));
    }

    [Fact]
    public void BandLine_CountsAboveTheCeiling_ForTheMetricsWhereHighIsTheConcern()
    {
        var series = Series(18, 21, 19, 22, 20, 17, 23);

        var line = TrendAwareness.BandLine(
            series, 20m, TrendAwareness.Direction.AboveUsual, "the recommended 20/min (WHO)", "day");

        Assert.Equal("Above the recommended 20/min (WHO) on 3 of the last 7 days", line);
    }

    /// <summary>Only the chart's own fortnight is counted, however long the series runs.</summary>
    [Fact]
    public void CountsOnlyTheFortnightTheChartDraws()
    {
        // 30 days, all under the usual — but only 14 are inside the window.
        var series = Series(Enumerable.Repeat<decimal?>(10, 30).ToArray());

        var line = TrendAwareness.Line(series, 20, TrendAwareness.Direction.BelowUsual, "day");

        Assert.Equal("Under their usual on 14 of the last 14 days", line);
    }
}

/// <summary>
/// The key line under a daybook chart must name only marks the chart drew, and a published
/// band must carry its publisher — a shaded band with nobody's name on it is a claim wearing no
/// authority.
/// </summary>
public class MetricChartKeyTests
{
    [Fact]
    public void NamesTheUsualAndTheSourcedBand()
    {
        var metric = new DashboardMetric
        {
            Baseline = 68,
            Reference = new MetricReference { Low = 60, High = 100, Source = "AHA" },
        };

        Assert.Equal(
            "Dashed: their usual 68  ·  Shaded: recommended 60–100 (AHA)",
            MetricChartKey.For(metric, "{0:N0}"));
    }

    [Fact]
    public void SaysNothing_WhenTheChartHasNeitherMark()
    {
        Assert.Null(MetricChartKey.For(new DashboardMetric(), "{0:N0}"));
    }

    [Fact]
    public void NamesOnlyTheUsual_ForAMetricNoBodyPublishesARangeFor()
    {
        var metric = new DashboardMetric { Baseline = 6000 };

        Assert.Equal("Dashed: their usual 6,000", MetricChartKey.For(metric, "{0:N0}"));
    }
}
