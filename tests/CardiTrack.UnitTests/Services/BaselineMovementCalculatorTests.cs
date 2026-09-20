using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Which of a member's metrics are worth telling their family about. This is the judgement the
/// card used to leave to the model, which answered it metric by metric — so what these pin is
/// that a metric sitting where it usually sits produces nothing at all, and that the bar is the
/// member's own spread rather than one number applied to everybody.
/// </summary>
public class BaselineMovementCalculatorTests
{
    private static readonly DateOnly Through = new(2026, 9, 20);
    private readonly Guid _memberId = Guid.NewGuid();

    [Fact]
    public void AMemberSittingAtTheirUsualHasNothingToSay()
    {
        // The case that produced seven bullets of "remained relatively stable". Nothing has moved,
        // so there is no card, and no model call is worth paying for.
        var movements = Compute(Baseline(avgSteps: 5000), steps: 5000)!;

        Assert.False(movements.HasAnythingToSay);
        Assert.Empty(movements.Notable);
        Assert.Contains("Steps", movements.Steady);
    }

    [Fact]
    public void AMetricWellAwayFromTheirUsualIsReported()
    {
        var movements = Compute(Baseline(avgSteps: 5000), steps: 3000)!;

        var steps = Assert.Single(movements.Notable);
        Assert.Equal("Steps", steps.Metric);
        Assert.Equal(3000, steps.Recent);
        Assert.Equal(5000, steps.Usual);
        Assert.Equal(-40, steps.DeviationPercent);
        Assert.DoesNotContain("Steps", movements.Steady);
    }

    /// <summary>
    /// The heart of it: the same movement is a finding for one member and ordinary for another,
    /// because the bar is their own learned variation. A fixed percentage cannot tell these apart.
    /// </summary>
    [Fact]
    public void TheSameMovementIsJudgedAgainstEachMembersOwnSpread()
    {
        // 71 against a usual 66 is five bpm either way. For the steady heart — two bpm of normal
        // variation — that is well past their own spread; for the variable one it is inside it.
        var steady = Compute(Baseline(avgRestingHeartRate: 66, stdDevHeartRate: 2m), restingHr: 71)!;
        var variable = Compute(Baseline(avgRestingHeartRate: 66, stdDevHeartRate: 9m), restingHr: 71)!;

        Assert.Contains(steady.Notable, m => m.Metric == "Resting heart rate");
        Assert.Contains("Resting heart rate", variable.Steady);
    }

    [Fact]
    public void AVeryNarrowSpreadStillHasToClearThePercentageFloor()
    {
        // A member whose readings barely move would otherwise have every rounding wobble reported:
        // one standard deviation of 0.2 bpm is a fifth of a beat. The floor is what stops the card
        // firing on noise, and it is why the bar is the larger of the two rather than the spread.
        var movements = Compute(
            Baseline(avgRestingHeartRate: 66, stdDevHeartRate: 0.2m), restingHr: 68)!;

        Assert.Contains("Resting heart rate", movements.Steady);
        Assert.Empty(movements.Notable);
    }

    /// <summary>
    /// Exactly at the bar is steady. The bar is what a movement has to pass, and
    /// <c>StatisticalAlertRules</c> draws the same boundary — <c>restingHr &lt;= average + margin</c>
    /// raises nothing — so a reading landing precisely on it must not be reported here either.
    /// </summary>
    [Fact]
    public void AMovementExactlyOnTheBarIsSteady()
    {
        // Usual 5,000, no learned spread, so the bar is the 15% fraction: 750 steps exactly.
        var onTheBar = Compute(Baseline(avgSteps: 5000), steps: 4250)!;
        var justPast = Compute(Baseline(avgSteps: 5000), steps: 4249)!;

        Assert.Contains("Steps", onTheBar.Steady);
        Assert.Empty(onTheBar.Notable);

        Assert.Contains(justPast.Notable, m => m.Metric == "Steps");
    }

    /// <summary>
    /// A week nothing could be judged in is not a week where all is well. Both come back with
    /// nothing to say, and the service treats them oppositely — one takes a standing concern down,
    /// the other leaves it alone — so the two have to be distinguishable here.
    /// </summary>
    [Fact]
    public void AnUnmeasuredWeekIsDistinguishableFromASteadyOne()
    {
        var steady = Compute(Baseline(avgSteps: 5000), steps: 5000)!;
        var unmeasured = BaselineMovementCalculator.Compute([], Baseline(avgSteps: 5000), Through)!;

        Assert.False(steady.HasAnythingToSay);
        Assert.True(steady.ShowsNothingIsOff);

        Assert.False(unmeasured.HasAnythingToSay);
        Assert.False(unmeasured.ShowsNothingIsOff);
        Assert.Contains("Steps", unmeasured.Unjudged);
    }

    /// <summary>
    /// Half a week's readings is not evidence that the other half is fine.
    /// </summary>
    /// <remarks>
    /// A watch reporting heart rate but no steps has judged something and said nothing whatever
    /// about steps. Treating that as "we looked and all is well" would let a partial sync outage
    /// retract a standing card about this member's steps on evidence that never addressed them.
    /// </remarks>
    [Fact]
    public void AMetricThatWentUnreadIsNotEvidenceThatItIsFine()
    {
        var movements = Compute(
            Baseline(avgSteps: 5000, avgRestingHeartRate: 66), restingHr: 66)!;

        // Heart rate was judged and held; steps has a usual and no readings at all.
        Assert.Contains("Resting heart rate", movements.Steady);
        Assert.Contains("Steps", movements.Unjudged);

        Assert.False(movements.HasAnythingToSay);
        Assert.False(movements.ShowsNothingIsOff);
    }

    [Fact]
    public void AMetricThisMemberHasNoUsualForIsNotAGap()
    {
        // Never learned means never measured for them, so waiting on it would mean never being
        // able to say all is well about anybody.
        var movements = Compute(Baseline(avgSteps: 5000), steps: 5000)!;

        Assert.Empty(movements.Unjudged);
        Assert.True(movements.ShowsNothingIsOff);
    }

    [Fact]
    public void TheRenderedBlockSaysWhichMetricsWentUnread()
    {
        // Unread is not steady. A metric in neither list would be a silence the model fills in.
        var rendered = BaselineMovementCalculator.Render(
            Compute(Baseline(avgSteps: 5000, avgRestingHeartRate: 66), steps: 3000)!);

        Assert.Contains("Too few readings this week to judge: Resting heart rate.", rendered);
        Assert.Contains("Say nothing about these.", rendered);
    }

    [Fact]
    public void AMetricWithNoLearnedSpreadFallsBackToTheFraction()
    {
        // Sleep and active minutes have no StdDev column on the baseline, so they are judged on
        // the fraction alone. 6 hours against a usual 8 is 25%, past the 15% floor.
        var movements = Compute(Baseline(avgSleepMinutes: 480), sleepMinutes: 360)!;

        var sleep = Assert.Single(movements.Notable);
        Assert.Equal("Sleep", sleep.Metric);

        // Hours, not minutes: these two figures are printed for a person to read.
        Assert.Equal("hours a night", sleep.Unit);
        Assert.Equal(6m, sleep.Recent);
        Assert.Equal(8m, sleep.Usual);
    }

    [Fact]
    public void AWeekTooSparseToJudgeIsNotJudged()
    {
        // Three days out of seven is not a week's average, and treating it as one would let a
        // single unusual morning decide whether a family is told something is wrong.
        var logs = Enumerable.Range(0, 3)
            .Select(offset => new ActivityLog
            {
                CardiMemberId = _memberId,
                Date = Through.AddDays(-offset),
                Steps = 1000,
            })
            .ToList();

        var movements = BaselineMovementCalculator.Compute(logs, Baseline(avgSteps: 5000), Through)!;

        Assert.Empty(movements.Notable);
        Assert.Empty(movements.Steady);
    }

    [Fact]
    public void TheWidestDepartureIsReportedFirst()
    {
        // Where several things have moved, a caregiver's attention should land on the one that
        // moved furthest rather than on whichever metric the calculator happens to list first.
        var movements = Compute(
            Baseline(avgSteps: 5000, avgSleepMinutes: 480),
            steps: 4000,
            sleepMinutes: 240)!;

        Assert.Equal(2, movements.Notable.Count);
        Assert.Equal("Sleep", movements.Notable[0].Metric);
        Assert.Equal("Steps", movements.Notable[1].Metric);
    }

    [Fact]
    public void AMemberWithNoEstablishedBaselineIsNotJudgedAtAll()
    {
        // The learning state, which is a different card and a different brief. There is no settled
        // usual here to call anything a departure from.
        Assert.Null(BaselineMovementCalculator.Compute([], baseline: null, Through));
    }

    [Fact]
    public void TwoDevicesOnOneDayDoNotCountTwice()
    {
        // Ingestion upserts per (DeviceConnection, Date), so a member wearing two watches has two
        // rows a day. Averaging raw rows would weight those days double and disagree with the
        // baseline, which collapses to the most recent row per date.
        var days = Enumerable.Range(0, 7).SelectMany(offset => new[]
        {
            new ActivityLog
            {
                CardiMemberId = _memberId,
                Date = Through.AddDays(-offset),
                Steps = 9000,
                CreatedDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            },
            new ActivityLog
            {
                CardiMemberId = _memberId,
                Date = Through.AddDays(-offset),
                Steps = 3000,
                CreatedDate = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc),
            },
        }).ToList();

        var movements = BaselineMovementCalculator.Compute(days, Baseline(avgSteps: 5000), Through)!;

        // The later row alone, not the mean of the two.
        Assert.Equal(3000, Assert.Single(movements.Notable).Recent);
    }

    /// <summary>
    /// The rendered block is what the model is allowed to say, so the steady metrics are named in
    /// it: without them the model cannot report that the rest held without counting for itself,
    /// and a metric left unmentioned reads as one that moved.
    /// </summary>
    [Fact]
    public void TheRenderedBlockNamesWhatMovedAndWhatDidNot()
    {
        var rendered = BaselineMovementCalculator.Render(
            Compute(Baseline(avgSteps: 5000, avgRestingHeartRate: 66), steps: 3000, restingHr: 66)!);

        Assert.Contains("Steps: averaging 3000 steps a day", rendered);
        Assert.Contains("against their usual 5000", rendered);
        Assert.Contains("40% below their usual", rendered);
        Assert.Contains("Measured and steady, nothing to report: Resting heart rate.", rendered);

        // The window and the baseline it is measured against, so neither is the model's to assume.
        Assert.Contains("7 days ending 2026-09-20", rendered);
        Assert.Contains("usual learned over 30 days", rendered);
    }

    /// <summary>
    /// The two calculators judge the same person from the same rows and must not drift apart about
    /// which metrics count — a metric this one silently dropped would be a metric the card can
    /// never report while the trend narrative still discusses it.
    /// </summary>
    [Fact]
    public void ItCoversTheSameMetricsTheTrendFeaturesDo()
    {
        // Both sides are read off real output rather than a declared list, because a declared list
        // is a third copy that can drift from the two it claims to describe.
        var everyMetric = Enumerable.Range(0, 40)
            .Select(offset => new ActivityLog
            {
                CardiMemberId = _memberId,
                Date = Through.AddDays(-offset),
                Steps = 5000,
                RestingHeartRate = 66,
                SleepMinutes = 480,
                ActiveMinutes = 30,
                HeartRateVariabilityMs = 45m,
                OvernightBreathingRate = 14m,
            })
            .ToList();

        var full = new PatternBaseline
        {
            CardiMemberId = _memberId,
            PeriodDays = 30,
            AvgSteps = 5000,
            AvgRestingHeartRate = 66,
            AvgSleepMinutes = 480,
            AvgActiveMinutes = 30,
            AvgHeartRateVariabilityMs = 45m,
            AvgOvernightBreathingRate = 14m,
        };

        var trend = TrendFeatureCalculator.Compute(everyMetric, [full], Through)!
            .Features.Select(f => f.Metric);

        var movements = BaselineMovementCalculator.Compute(everyMetric, full, Through)!;
        var judged = movements.Notable.Select(m => m.Metric).Concat(movements.Steady);

        Assert.Equal(trend.OrderBy(m => m), judged.OrderBy(m => m));
    }

    private BaselineMovements? Compute(
        PatternBaseline baseline,
        int? steps = null,
        int? restingHr = null,
        int? sleepMinutes = null)
    {
        var logs = Enumerable.Range(0, BaselineMovementCalculator.RecentDays)
            .Select(offset => new ActivityLog
            {
                CardiMemberId = _memberId,
                Date = Through.AddDays(-offset),
                Steps = steps,
                RestingHeartRate = restingHr,
                SleepMinutes = sleepMinutes,
            })
            .ToList();

        return BaselineMovementCalculator.Compute(logs, baseline, Through);
    }

    private PatternBaseline Baseline(
        int? avgSteps = null,
        int? avgRestingHeartRate = null,
        decimal? stdDevHeartRate = null,
        int? avgSleepMinutes = null) => new()
    {
        CardiMemberId = _memberId,
        PeriodDays = 30,
        AvgSteps = avgSteps,
        AvgRestingHeartRate = avgRestingHeartRate,
        StdDevHeartRate = stdDevHeartRate,
        AvgSleepMinutes = avgSleepMinutes,
    };
}
