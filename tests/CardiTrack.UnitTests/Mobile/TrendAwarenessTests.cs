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
    public void CountsTheDaysBelowTheUsual_AndCarriesTheUsualsFigureInTheSentence()
    {
        // 10 of 14 nights under a 420-minute usual; the four at 430-460 are the other side. The
        // caller hands the usual in with its figure and unit, so the claim stands on its own.
        var series = Series(400, 410, 430, 390, 380, 440, 370, 360, 350, 450, 340, 330, 460, 320);

        var line = TrendAwareness.Line(
            series, 420, TrendAwareness.Direction.BelowUsual, "their usual 7h", "night");

        Assert.Equal("Under their usual 7h on 10 of the last 14 nights", line);
    }

    [Fact]
    public void CountsTheDaysAboveTheUsual_ForTheMetricsWhereHighIsTheConcern()
    {
        var series = Series(70, 72, 68, 75, 74, 66, 71);

        var line = TrendAwareness.Line(
            series, 69, TrendAwareness.Direction.AboveUsual, "their usual 69 bpm", "day");

        Assert.Equal("Above their usual 69 bpm on 5 of the last 7 days", line);
    }

    /// <summary>No baseline means no claim — a count against an invented usual would be the
    /// fabricated normal this product refuses everywhere else.</summary>
    [Fact]
    public void SaysNothing_WithoutABaseline()
    {
        Assert.Null(TrendAwareness.Line(
            Series(1, 2, 3, 4, 5, 6, 7), null,
            TrendAwareness.Direction.BelowUsual, "their usual", "day"));
    }

    /// <summary>
    /// A count of zero is the sentence a caregiver most wants to take in at a glance, and "0 of
    /// the last 14" buries it in arithmetic — so it is said the way a person would say it.
    /// </summary>
    [Fact]
    public void SaysNotOnce_WhenNoDayIsOnTheCountedSide()
    {
        var series = Series(70, 72, 68, 75, 74, 66, 71);

        var line = TrendAwareness.Line(
            series, 80, TrendAwareness.Direction.AboveUsual, "their usual 80 bpm", "day");

        Assert.Equal("Not once above their usual 80 bpm in the last 7 days", line);
    }

    /// <summary>
    /// Below the floor the sentence would be counting noise: "under their usual on 2 of the last
    /// 3 nights" reads as a pattern and is a long weekend.
    /// </summary>
    [Fact]
    public void SaysNothing_WithTooFewMeasuredDays()
    {
        var series = Series(100, null, null, 90, null, 80, null, null, null, 70, null, 60, 50, null);

        Assert.Null(TrendAwareness.Line(
            series, 95, TrendAwareness.Direction.BelowUsual, "their usual", "day"));
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

        var line = TrendAwareness.Line(
            series, 600, TrendAwareness.Direction.BelowUsual, "their usual", "day");

        // Nine finished days counted, all of them under; the partial tenth is not on either side
        // of the sentence. A full count is said as "every one" rather than "9 of the last 9".
        Assert.Equal("Under their usual on every one of the last 9 days", line);
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

    /// <summary>
    /// The reassuring band count — a heart rate that never crossed the published ceiling — reads
    /// as the good news it is, not as "0 of the last 14 days".
    /// </summary>
    [Fact]
    public void BandLine_SaysNotOnce_WhenTheBoundWasNeverCrossed()
    {
        var series = Series(70, 72, 68, 75, 74, 66, 71);

        var line = TrendAwareness.BandLine(
            series, 100m, TrendAwareness.Direction.AboveUsual, "the recommended 100 bpm (AHA)", "day");

        Assert.Equal("Not once above the recommended 100 bpm (AHA) in the last 7 days", line);
    }

    /// <summary>Only the chart's own fortnight is counted, however long the series runs.</summary>
    [Fact]
    public void CountsOnlyTheFortnightTheChartDraws()
    {
        // 30 days, all under the usual — but only 14 are inside the window.
        var series = Series(Enumerable.Repeat<decimal?>(10, 30).ToArray());

        var line = TrendAwareness.Line(
            series, 20, TrendAwareness.Direction.BelowUsual, "their usual", "day");

        Assert.Equal("Under their usual on every one of the last 14 days", line);
    }

    /// <summary>
    /// A daybook is a closure summary: its charts end on the day the entry reviews, and days
    /// after it — today's running total included — are of no consequence and never drawn.
    /// </summary>
    [Fact]
    public void FortnightEndingOn_EndsOnTheReviewedDay_AndDropsEveryDayAfterIt()
    {
        // A 30-point series running to Today, reviewing yesterday: the window is the fortnight
        // ending yesterday, and today's point is not in it.
        var series = Series(Enumerable.Range(1, 30).Select(v => (decimal?)v).ToArray());
        var reviewedDate = Today.AddDays(-1);

        var window = TrendAwareness.FortnightEndingOn(series, reviewedDate);

        Assert.NotNull(window);
        Assert.Equal(TrendAwareness.WindowDays, window.Count);
        Assert.Equal(reviewedDate, window[^1].Date);
        Assert.Equal(reviewedDate.AddDays(-(TrendAwareness.WindowDays - 1)), window[0].Date);
    }

    /// <summary>
    /// An old entry keeps its own fortnight for as long as the series still reaches it — the
    /// window ends on the reviewed day, not on whatever day the reader opened the entry.
    /// </summary>
    [Fact]
    public void FortnightEndingOn_WindowsAnOldEntryToItsOwnDay()
    {
        var series = Series(Enumerable.Range(1, 30).Select(v => (decimal?)v).ToArray());
        // The oldest entry a 30-day series still fully covers: 16 days back.
        var reviewedDate = Today.AddDays(-16);

        var window = TrendAwareness.FortnightEndingOn(series, reviewedDate);

        Assert.NotNull(window);
        Assert.Equal(reviewedDate, window[^1].Date);
    }

    /// <summary>
    /// When the series has moved on past an entry's fortnight, the answer is no window at all —
    /// a chart truncated at the series' edge would shift the account toward the present, the one
    /// direction a closed day must not drift.
    /// </summary>
    [Fact]
    public void FortnightEndingOn_SaysNothing_WhenTheSeriesNoLongerReachesTheEntry()
    {
        var series = Series(Enumerable.Range(1, 30).Select(v => (decimal?)v).ToArray());

        Assert.Null(TrendAwareness.FortnightEndingOn(series, Today.AddDays(-17)));
    }

    /// <summary>A missing series is no window, not a crash on the charts' way out.</summary>
    [Fact]
    public void FortnightEndingOn_TreatsANullSeriesAsNoWindow()
    {
        Assert.Null(TrendAwareness.FortnightEndingOn(null, Today));
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
    public void NamesTheUsualAndTheSourcedBand_EachFigureWithItsUnit()
    {
        var metric = new DashboardMetric
        {
            Baseline = 68,
            Reference = new MetricReference { Low = 60, High = 100, Source = "AHA" },
        };

        Assert.Equal(
            "Dashed: their usual 68 bpm  ·  Shaded: recommended 60–100 bpm (AHA)",
            MetricChartKey.For(metric, "{0:N0}", " bpm"));
    }

    [Fact]
    public void SaysNothing_WhenTheChartHasNeitherMark()
    {
        Assert.Null(MetricChartKey.For(new DashboardMetric(), "{0:N0}", " bpm"));
    }

    [Fact]
    public void NamesOnlyTheUsual_ForAMetricNoBodyPublishesARangeFor()
    {
        var metric = new DashboardMetric { Baseline = 6000 };

        Assert.Equal(
            "Dashed: their usual 6,000 steps", MetricChartKey.For(metric, "{0:N0}", " steps"));
    }
}
