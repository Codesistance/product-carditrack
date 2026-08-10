using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins every rule to its boundary. The thresholds are the product's hard-coded "medium"
/// sensitivity, and the null-vs-zero discipline is load-bearing: a day the device did not
/// measure must never read as a day the member did nothing.
/// </summary>
public class StatisticalAlertRulesTests
{
    private static PatternBaseline Baseline() => new()
    {
        CardiMemberId = Guid.NewGuid(),
        PeriodDays = 30,
        AvgSteps = 6000,
        StdDevSteps = 900,
        AvgRestingHeartRate = 62,
        StdDevHeartRate = 2.0m,
        AvgSleepMinutes = 420,
        TypicalWakeTime = new TimeOnly(7, 30),
    };

    private static ActivityLog Log(int? steps = null, int? restingHr = null, int? sleepMinutes = null) => new()
    {
        Date = new DateOnly(2026, 8, 9),
        Steps = steps,
        RestingHeartRate = restingHr,
        SleepMinutes = sleepMinutes,
    };

    // ── activity_decline ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ActivityDecline_Fires_JustBelowSeventyPercentOfBaseline()
    {
        var candidate = StatisticalAlertRules.ActivityDecline(Baseline(), Log(steps: 4199));

        Assert.NotNull(candidate);
        Assert.Equal(AlertType.Inactivity, candidate.Type);
        Assert.Equal(AlertSeverity.Yellow, candidate.Severity);
        Assert.Contains("\"rule\":\"activity_decline\"", candidate.MetricValues);
    }

    // 70% of 6000 is 4200 — the boundary itself is not a decline.
    [Theory]
    [InlineData(4200)]
    [InlineData(6000)]
    public void ActivityDecline_StaysQuiet_AtOrAboveTheBoundary(int steps)
    {
        Assert.Null(StatisticalAlertRules.ActivityDecline(Baseline(), Log(steps: steps)));
    }

    [Fact]
    public void ActivityDecline_TreatsAnUnmeasuredDayAsNothing()
    {
        Assert.Null(StatisticalAlertRules.ActivityDecline(Baseline(), Log(steps: null)));
        Assert.Null(StatisticalAlertRules.ActivityDecline(Baseline(), yesterday: null));
    }

    [Fact]
    public void ActivityDecline_NeedsAStepsBaseline()
    {
        var baseline = Baseline();
        baseline.AvgSteps = null;

        Assert.Null(StatisticalAlertRules.ActivityDecline(baseline, Log(steps: 100)));
    }

    // ── irregular_sleep ──────────────────────────────────────────────────────────────────

    // 30% of 420 min is 126 — both directions past it are irregular.
    [Theory]
    [InlineData(293, "less")]
    [InlineData(547, "more")]
    public void IrregularSleep_Fires_InEitherDirection(int sleep, string direction)
    {
        var candidate = StatisticalAlertRules.IrregularSleep(Baseline(), Log(sleepMinutes: sleep));

        Assert.NotNull(candidate);
        Assert.Equal(AlertType.Sleep, candidate.Type);
        Assert.Contains(direction, candidate.Message);
    }

    [Theory]
    [InlineData(294)]
    [InlineData(420)]
    [InlineData(546)]
    public void IrregularSleep_StaysQuiet_WithinTheBand(int sleep)
    {
        Assert.Null(StatisticalAlertRules.IrregularSleep(Baseline(), Log(sleepMinutes: sleep)));
    }

    // ── elevated_heart_rate ──────────────────────────────────────────────────────────────

    // σ = 2 → 2σ = 4 < the 5 bpm floor, so the margin is 5: fires above 67, not at it.
    [Fact]
    public void ElevatedHeartRate_UsesTheFloor_WhenSigmaIsTight()
    {
        Assert.Null(StatisticalAlertRules.ElevatedHeartRate(Baseline(), Log(restingHr: 67)));

        var candidate = StatisticalAlertRules.ElevatedHeartRate(Baseline(), Log(restingHr: 68));
        Assert.NotNull(candidate);
        Assert.Equal(AlertType.HeartRate, candidate.Type);
        Assert.Equal(AlertSeverity.Orange, candidate.Severity);
    }

    // σ = 6 → 2σ = 12 beats the floor: 62 + 12 = 74 is the boundary.
    [Fact]
    public void ElevatedHeartRate_UsesTwoSigma_WhenItIsWider()
    {
        var baseline = Baseline();
        baseline.StdDevHeartRate = 6.0m;

        Assert.Null(StatisticalAlertRules.ElevatedHeartRate(baseline, Log(restingHr: 74)));
        Assert.NotNull(StatisticalAlertRules.ElevatedHeartRate(baseline, Log(restingHr: 75)));
    }

    [Fact]
    public void ElevatedHeartRate_WithNoSigmaRecorded_StillHasTheFloor()
    {
        var baseline = Baseline();
        baseline.StdDevHeartRate = null;

        Assert.Null(StatisticalAlertRules.ElevatedHeartRate(baseline, Log(restingHr: 67)));
        Assert.NotNull(StatisticalAlertRules.ElevatedHeartRate(baseline, Log(restingHr: 68)));
    }

    // ── no_morning_activity ──────────────────────────────────────────────────────────────

    // Wake 07:30 + 2h grace → the rule arms at 09:30 local.
    [Fact]
    public void NoMorningActivity_Fires_OnAMeasuredZero_PastWakePlusGrace()
    {
        var candidate = StatisticalAlertRules.NoMorningActivity(
            Baseline(), Log(steps: 0), new DateTime(2026, 8, 10, 9, 30, 0));

        Assert.NotNull(candidate);
        Assert.Equal(AlertType.PatternBreak, candidate.Type);
        Assert.Equal(AlertSeverity.Red, candidate.Severity);
    }

    [Fact]
    public void NoMorningActivity_StaysQuiet_DuringTheGrace()
    {
        Assert.Null(StatisticalAlertRules.NoMorningActivity(
            Baseline(), Log(steps: 0), new DateTime(2026, 8, 10, 9, 29, 0)));
    }

    // The red severity makes this the rule where null-vs-zero matters most: an HR-only device
    // reports no steps field at all, and that absence must never page a family.
    [Fact]
    public void NoMorningActivity_NeverFires_OnAnUnmeasuredSteps()
    {
        Assert.Null(StatisticalAlertRules.NoMorningActivity(
            Baseline(), Log(steps: null), new DateTime(2026, 8, 10, 12, 0, 0)));
        Assert.Null(StatisticalAlertRules.NoMorningActivity(
            Baseline(), today: null, new DateTime(2026, 8, 10, 12, 0, 0)));
    }

    [Fact]
    public void NoMorningActivity_NeedsATypicalWakeTime()
    {
        var baseline = Baseline();
        baseline.TypicalWakeTime = null;

        Assert.Null(StatisticalAlertRules.NoMorningActivity(
            baseline, Log(steps: 0), new DateTime(2026, 8, 10, 12, 0, 0)));
    }

    // ── long_term_trend ──────────────────────────────────────────────────────────────────

    private static Dictionary<DateOnly, ActivityLog> WeeklySteps(
        DateOnly yesterday, params int[] weeklyAveragesNewestFirst)
    {
        var logs = new Dictionary<DateOnly, ActivityLog>();
        for (var week = 0; week < weeklyAveragesNewestFirst.Length; week++)
        {
            for (var offset = 0; offset < 7; offset++)
            {
                var date = yesterday.AddDays(-7 * week - offset);
                logs[date] = new ActivityLog { Date = date, Steps = weeklyAveragesNewestFirst[week] };
            }
        }

        return logs;
    }

    private static readonly DateOnly Yesterday = new(2026, 8, 9);

    [Fact]
    public void LongTermTrend_Fires_OnFourWeeksOfSustainedDecline()
    {
        // Each week ≥5% below the one before: 7000 → 6600 → 6200 → 5800.
        var logs = WeeklySteps(Yesterday, 5800, 6200, 6600, 7000);

        var candidate = StatisticalAlertRules.LongTermTrend(logs, Yesterday);

        Assert.NotNull(candidate);
        Assert.Equal(AlertType.Trend, candidate.Type);
        Assert.Equal(AlertSeverity.Orange, candidate.Severity);
        Assert.Contains("\"rule\":\"long_term_trend\"", candidate.MetricValues);
    }

    // One recovering week breaks the pattern — a sustained trend, not three bad patches.
    [Fact]
    public void LongTermTrend_StaysQuiet_WhenOneWeekRecovers()
    {
        var logs = WeeklySteps(Yesterday, 5800, 6900, 6600, 7000);

        Assert.Null(StatisticalAlertRules.LongTermTrend(logs, Yesterday));
    }

    // A 4% weekly decline is ordinary drift, not a trend.
    [Fact]
    public void LongTermTrend_StaysQuiet_BelowTheWeeklyThreshold()
    {
        var logs = WeeklySteps(Yesterday, 6367, 6632, 6908, 7196);

        Assert.Null(StatisticalAlertRules.LongTermTrend(logs, Yesterday));
    }

    [Fact]
    public void LongTermTrend_NeedsEnoughMeasuredDays_InEveryWeek()
    {
        var logs = WeeklySteps(Yesterday, 5800, 6200, 6600, 7000);
        // Hollow out the oldest week to 3 measured days.
        for (var offset = 0; offset < 4; offset++)
            logs.Remove(Yesterday.AddDays(-21 - offset));

        Assert.Null(StatisticalAlertRules.LongTermTrend(logs, Yesterday));
    }

    [Fact]
    public void LongTermTrend_IgnoresUnmeasuredDays_RatherThanCountingThemAsZero()
    {
        var logs = WeeklySteps(Yesterday, 5800, 6200, 6600, 7000);
        // Null out one day per week: averages must be unaffected, so it still fires.
        foreach (var week in Enumerable.Range(0, 4))
            logs[Yesterday.AddDays(-7 * week)].Steps = null;

        Assert.NotNull(StatisticalAlertRules.LongTermTrend(logs, Yesterday));
    }
}
