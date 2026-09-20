using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The deterministic half of the trend pass. Every figure the narrative quotes comes from here, so
/// what these pin is that each one is arithmetic over the member's own rows — and that the pass
/// says nothing at all before there is enough of them.
/// </summary>
public class TrendFeatureCalculatorTests
{
    private static readonly DateOnly Through = new(2026, 9, 20);
    private readonly Guid _memberId = Guid.NewGuid();

    [Fact]
    public void AMemberWithLessThanAMonthOfReadingsGetsNoFeaturesAtAll()
    {
        // The cold start the design names. A line fitted through a new wearer's first fortnight
        // describes them getting used to the watch, not a trajectory.
        var logs = Days(TrendFeatureCalculator.MinimumDaysForTrend - 1, day => 5000);

        Assert.Null(TrendFeatureCalculator.Compute(logs, [], Through));
    }

    [Fact]
    public void AMemberOnTheThresholdIsNarrated()
    {
        var logs = Days(TrendFeatureCalculator.MinimumDaysForTrend, day => 5000);

        var features = TrendFeatureCalculator.Compute(logs, [], Through);

        Assert.NotNull(features);
        Assert.Equal(TrendFeatureCalculator.MinimumDaysForTrend, features!.DaysOfHistory);
        Assert.Equal(Through, features.Through);
    }

    [Fact]
    public void DaysCarryingNoTrendMetricDoNotCountTowardsTheColdStart()
    {
        // Sixty rows holding only distance and blood oxygen — neither of which this calculator
        // reads. Counting rows would clear the gate, every feature would come back empty, and the
        // pass would spend a model call asking for a narrative of no figures at all.
        var logs = Enumerable.Range(0, 60)
            .Select(offset => new ActivityLog
            {
                CardiMemberId = _memberId,
                Date = Through.AddDays(-(59 - offset)),
                Distance = 3.2m,
                SpO2Average = 96,
            })
            .ToList();

        Assert.Null(TrendFeatureCalculator.Compute(logs, [], Through));
    }

    [Fact]
    public void AHistoryWithNothingInTheLastWeekIsDeclined()
    {
        // Plenty of history, but the recent window is entirely unmeasured: there is a header and
        // nothing to put under it, which is not a trajectory.
        var logs = Days(60, day => day < 40 ? 4000 : null);

        Assert.Null(TrendFeatureCalculator.Compute(logs, [], Through));
    }

    [Fact]
    public void TheRecentAverageIsTheLastSevenMeasuredDays_NotTheWholeWindow()
    {
        // 2,000 steps for weeks, then a week at 6,000. "Recently" has to mean the week, or a
        // member who has just stopped moving reads as unchanged for a month.
        var logs = Days(60, day => day >= 53 ? 6000 : 2000);

        var steps = Feature(logs, [], "Steps");

        Assert.Equal(6000, steps.RecentAverage);
        Assert.Equal(TrendFeatureCalculator.MovingAverageDays, steps.MeasuredDays);
    }

    [Fact]
    public void TheSlopeCarriesTheDirection()
    {
        var falling = Feature(Days(60, day => 8000 - day * 50), [], "Steps");
        var rising = Feature(Days(60, day => 2000 + day * 50), [], "Steps");
        var flat = Feature(Days(60, day => 4000), [], "Steps");

        Assert.True(falling.ChangePerDay < 0);
        Assert.True(rising.ChangePerDay > 0);
        Assert.Equal(0, flat.ChangePerDay);
    }

    [Fact]
    public void TooFewMeasuredDaysInTheSlopeWindowLeavesTheSlopeUnstated()
    {
        // Thirty days of history, but only a handful of them inside the four-week slope window
        // carry a step count. Silence rather than a line through three points.
        var logs = Days(40, day => day % 9 == 0 ? 4000 : null);

        var steps = TrendFeatureCalculator.Compute(logs, [], Through)?.Features
            .FirstOrDefault(f => f.Metric == "Steps");

        Assert.Null(steps?.ChangePerDay);
    }

    [Fact]
    public void EachBaselineWindowGetsItsOwnDeviation()
    {
        // The shape of a slow drift: level against the recent normal, well down on the older one.
        var logs = Days(60, day => 4000);
        var baselines = new[]
        {
            Baseline(30, avgSteps: 4000),
            Baseline(90, avgSteps: 8000),
        };

        var steps = Feature(logs, baselines, "Steps");

        Assert.Equal(0, steps.DeviationPercent[30]);
        Assert.Equal(-50, steps.DeviationPercent[90]);
    }

    [Fact]
    public void AWindowThatLearnedNothingForAMetricIsLeftOut_NotReportedAsNoChange()
    {
        // Nothing learned is not the same as no change, and a zero here would read as the second.
        var logs = Days(60, day => 4000);
        var baselines = new[] { Baseline(30, avgSteps: null) };

        var steps = Feature(logs, baselines, "Steps");

        Assert.Empty(steps.DeviationPercent);
    }

    [Fact]
    public void TheRenderedBlockQuotesOnlyComputedFigures()
    {
        var logs = Days(60, day => 4000);
        var features = TrendFeatureCalculator.Compute(logs, [Baseline(30, avgSteps: 5000)], Through)!;

        var rendered = TrendFeatureCalculator.Render(features);

        Assert.Contains("60 days with readings", rendered);
        Assert.Contains("Steps: last 7 days averaged 4000 steps a day", rendered);
        Assert.Contains("20% below their 30-day usual", rendered);
    }

    /// <summary>
    /// Ingestion upserts per (DeviceConnection, Date), so a member wearing two watches has two
    /// rows a day. Every figure here is a per-day one, and a second row must change none of them.
    /// </summary>
    [Fact]
    public void ASecondDeviceRowForTheSameDayChangesNothing()
    {
        var days = Days(40, _ => 8_000);
        var baselines = new List<PatternBaseline> { Baseline(30, 8_000) };

        var single = TrendFeatureCalculator.Compute(days, baselines, Through)!;

        // The same days again, written later, with a figure a straight average would be dragged
        // toward. The newer row per date is the one that counts, exactly as the baseline reads it.
        var doubled = days
            .Select(day => new ActivityLog
            {
                CardiMemberId = _memberId,
                Date = day.Date,
                Steps = 2_000,
                CreatedDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            })
            .Concat(days.Select(day => new ActivityLog
            {
                CardiMemberId = _memberId,
                Date = day.Date,
                Steps = day.Steps,
                CreatedDate = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc),
            }))
            .ToList();

        var collapsed = TrendFeatureCalculator.Compute(doubled, baselines, Through)!;

        Assert.Equal(single.DaysOfHistory, collapsed.DaysOfHistory);
        Assert.Equal(
            single.Features.Single(f => f.Metric == "Steps").RecentAverage,
            collapsed.Features.Single(f => f.Metric == "Steps").RecentAverage);
    }

    /// <summary>
    /// The cold-start gate counts dates, so a fortnight on two watches must not clear a month.
    /// </summary>
    [Fact]
    public void AFortnightOnTwoDevicesIsStillAFortnight()
    {
        var days = Days(20, _ => 8_000);
        var doubled = days
            .Concat(days.Select(day => new ActivityLog
            {
                CardiMemberId = _memberId,
                Date = day.Date,
                Steps = 8_000,
                CreatedDate = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc),
            }))
            .ToList();

        Assert.Equal(40, doubled.Count);
        Assert.Null(TrendFeatureCalculator.Compute(doubled, [], Through));
    }

    private TrendFeature Feature(
        IReadOnlyList<ActivityLog> logs, IReadOnlyList<PatternBaseline> baselines, string metric) =>
        TrendFeatureCalculator.Compute(logs, baselines, Through)!.Features.Single(f => f.Metric == metric);

    /// <summary>
    /// <paramref name="steps"/> is called with the day's index, oldest first, so a case can shape
    /// the series it needs without spelling out sixty rows.
    /// </summary>
    private List<ActivityLog> Days(int count, Func<int, int?> steps) =>
        Enumerable.Range(0, count)
            .Select(offset => new ActivityLog
            {
                CardiMemberId = _memberId,
                Date = Through.AddDays(-(count - 1 - offset)),
                Steps = steps(offset),
            })
            .ToList();

    private PatternBaseline Baseline(int periodDays, int? avgSteps) => new()
    {
        CardiMemberId = _memberId,
        PeriodDays = periodDays,
        AvgSteps = avgSteps,
    };
}
