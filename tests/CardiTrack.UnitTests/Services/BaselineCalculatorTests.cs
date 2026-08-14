using System.Text.Json;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The baseline is what ends a member's learning phase and what every "is today normal?" judgement is
/// measured against, so these cases pin the rules that decide when one is written and what it says.
/// </summary>
public class BaselineCalculatorTests
{
    private const int Period = 30;

    /// <summary>80% of a 30-day window.</summary>
    private const int RequiredDays = 24;

    private static readonly Guid MemberId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 8, 2);
    private static readonly IDescriptiveStatistics Stats = new MathNetDescriptiveStatistics();

    private static PatternBaseline? Calc(
        IReadOnlyList<ActivityLog> logs, int periodDays, DateOnly windowEnd) =>
        BaselineCalculator.Calculate(MemberId, logs, periodDays, windowEnd, Stats);

    /// <summary>
    /// <paramref name="dayCount"/> consecutive covered days ending on <see cref="Today"/>. Every day
    /// carries a resting heart rate so it counts toward coverage without disturbing whichever metric a
    /// test is actually asserting on.
    /// </summary>
    private static List<ActivityLog> Window(int dayCount, DateOnly? endingOn = null)
    {
        var end = endingOn ?? Today;
        return Enumerable.Range(0, dayCount)
            .Select(offset => new ActivityLog
            {
                CardiMemberId = MemberId,
                Date = end.AddDays(-offset),
                RestingHeartRate = 60,
            })
            .ToList();
    }

    // ── Coverage gate ───────────────────────────────────────────────────────────

    [Fact]
    public void Calculate_ReturnsNull_WhenTheWindowIsTooSparse()
    {
        Assert.Null(Calc(Window(RequiredDays - 1), Period, Today));
    }

    [Fact]
    public void Calculate_ReturnsBaseline_AtExactlyTheRequiredCoverage()
    {
        var baseline = Calc(Window(RequiredDays), Period, Today);

        Assert.NotNull(baseline);
        Assert.Equal(MemberId, baseline.CardiMemberId);
        Assert.Equal(Period, baseline.PeriodDays);
    }

    [Fact]
    public void Calculate_IgnoresLogsOutsideTheWindow()
    {
        // Enough days in total, but only 20 of them fall inside the 30-day window.
        var logs = Window(20);
        logs.AddRange(Window(20, Today.AddDays(-60)));

        Assert.Null(Calc(logs, Period, Today));
    }

    [Fact]
    public void Calculate_CountsADayOnce_WhenTwoDevicesReportIt()
    {
        // 24 days of data spread over 23 dates: the duplicate must not buy the member coverage.
        var logs = Window(RequiredDays - 1);
        logs.Add(new ActivityLog { CardiMemberId = MemberId, Date = Today, RestingHeartRate = 71 });

        Assert.Null(Calc(logs, Period, Today));
    }

    [Fact]
    public void Calculate_PrefersTheMostRecentlyWrittenRow_ForADuplicatedDay()
    {
        var logs = Window(RequiredDays);
        logs[0].Steps = 1_000;
        logs[0].UpdatedDate = new DateTime(2026, 8, 2, 6, 0, 0, DateTimeKind.Utc);
        logs[7].Steps = 9_000;  // same weekday as Today, so both land in one bucket
        logs.Add(new ActivityLog
        {
            CardiMemberId = MemberId,
            Date = Today,
            Steps = 9_000,
            UpdatedDate = new DateTime(2026, 8, 2, 18, 0, 0, DateTimeKind.Utc),
        });

        // Too few step samples overall for an average, so the weekday bucket is what exposes which of
        // the two rows for Today was chosen: 9000 if the later write won, 5000 if the earlier one did.
        var baseline = Calc(logs, Period, Today);

        Assert.NotNull(baseline);
        var byDayOfWeek = JsonSerializer.Deserialize<int?[]>(baseline.StepsByDayOfWeek!)!;
        Assert.Contains(9_000, byDayOfWeek);
        Assert.DoesNotContain(5_000, byDayOfWeek);
    }

    [Fact]
    public void Calculate_RejectsANonPositivePeriod()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Calc(Window(RequiredDays), 0, Today));
    }

    // ── Provisional windows ─────────────────────────────────────────────────────
    //
    // The 7- and 14-day windows exist so an early, low-confidence picture can colour the
    // dashboard while the 30-day window is still filling. Their per-metric floor scales with
    // the window: a flat 7 would demand a perfect week from exactly the window that exists to
    // tolerate an imperfect first one.

    [Fact]
    public void Calculate_WritesASevenDayBaseline_FromSixCoveredDays()
    {
        var baseline = Calc(Window(6), 7, Today);

        Assert.NotNull(baseline);
        Assert.Equal(7, baseline.PeriodDays);
        // Six samples meet the 7-day window's scaled floor, so the metric is averaged, not nulled.
        Assert.Equal(60, baseline.AvgRestingHeartRate);
    }

    [Fact]
    public void Calculate_ReturnsNull_WhenTheSevenDayWindowHasOnlyFiveDays()
    {
        Assert.Null(Calc(Window(5), 7, Today));
    }

    [Fact]
    public void Calculate_KeepsTheFullMetricFloor_ForTheFourteenDayWindow()
    {
        // ceil(14 × 0.8) = 12 covered days clear the coverage gate, but only 6 of them carry
        // steps — one short of the 14-day window's unscaled floor of 7 samples.
        var logs = Window(12);
        for (var i = 0; i < 6; i++)
            logs[i].Steps = 4_000;

        var baseline = Calc(logs, 14, Today);

        Assert.NotNull(baseline);
        Assert.Null(baseline.AvgSteps);
        Assert.Equal(60, baseline.AvgRestingHeartRate);
    }

    [Fact]
    public void SupportedPeriods_CoverProvisionalAndEstablishedWindows()
    {
        Assert.Equal(new[] { 7, 14, 30, 60, 90 }, BaselineCalculator.SupportedPeriods);
    }

    // ── Averages and spread ─────────────────────────────────────────────────────

    [Fact]
    public void Calculate_UsesTheSampleStandardDeviation()
    {
        var logs = Window(RequiredDays);
        int[] steps = [2_000, 4_000, 4_000, 4_000, 5_000, 5_000, 7_000, 9_000];
        for (var i = 0; i < steps.Length; i++)
            logs[i].Steps = steps[i];

        var baseline = Calc(logs, Period, Today);

        Assert.NotNull(baseline);
        Assert.Equal(5_000, baseline.AvgSteps);
        // n−1 gives 2138.09; the population form would report 2000 and quietly narrow the
        // "normal range" the dashboard draws from it.
        Assert.Equal(2138.09m, baseline.StdDevSteps);
        // Eight samples, even count: median is the average of 4_000 and 5_000.
        Assert.Equal(4_500, baseline.MedianSteps);
        // |x − 4500|: 2500, 500, 500, 500, 500, 500, 2500, 4500 → median 500.
        Assert.Equal(500.00m, baseline.MadSteps);
    }

    [Fact]
    public void Calculate_PersistsMedianAndMad_WithoutChangingTheMean()
    {
        // 7 ordinary 5_000-step days plus one 20_000-step day. Mean/σ shift; median/MAD stay
        // on the ordinary cluster. Live R1 still uses the mean — these columns are additive.
        var logs = Window(RequiredDays);
        for (var i = 0; i < 7; i++)
            logs[i].Steps = 5_000;
        logs[7].Steps = 20_000;

        var baseline = Calc(logs, Period, Today);

        Assert.NotNull(baseline);
        Assert.Equal(6_875, baseline.AvgSteps);
        Assert.Equal(5_000, baseline.MedianSteps);
        Assert.Equal(0.00m, baseline.MadSteps);
    }

    [Fact]
    public void Calculate_LeavesAThinlySampledMetricNull_WhileTheRestPopulate()
    {
        var logs = Window(RequiredDays);
        for (var i = 0; i < 6; i++)
            logs[i].SleepMinutes = 420;

        var baseline = Calc(logs, Period, Today);

        Assert.NotNull(baseline);
        Assert.Null(baseline.AvgSleepMinutes);
        Assert.Null(baseline.MedianSleepMinutes);
        Assert.Null(baseline.MadSleepMinutes);
        Assert.Equal(60, baseline.AvgRestingHeartRate);
    }

    [Fact]
    public void Calculate_ReportsTheObservedMaximumHeartRate_WithoutASampleFloor()
    {
        var logs = Window(RequiredDays);
        logs[0].MaxHeartRate = 142;
        logs[1].MaxHeartRate = 118;

        var baseline = Calc(logs, Period, Today);

        Assert.NotNull(baseline);
        Assert.Equal(142, baseline.MaxHeartRateObserved);
    }

    [Fact]
    public void Calculate_LeavesTheObservedMaximumNull_WhenNoReadingCarriesOne()
    {
        // Max() over int? returns null for an empty sequence rather than throwing, unlike the
        // non-nullable overload. Pinned because the difference is easy to misread.
        var baseline = Calc(Window(RequiredDays), Period, Today);

        Assert.NotNull(baseline);
        Assert.Null(baseline.MaxHeartRateObserved);
    }

    // ── Clock times ─────────────────────────────────────────────────────────────

    [Fact]
    public void Calculate_AveragesBedtimesAcrossMidnight()
    {
        var logs = Window(RequiredDays);
        for (var i = 0; i < 8; i++)
        {
            // Alternating 23:00 and 01:00 — an arithmetic mean of these is midday.
            var hour = i % 2 == 0 ? 23 : 1;
            logs[i].SleepStartTime = logs[i].Date.ToDateTime(new TimeOnly(hour, 0), DateTimeKind.Utc);
        }

        var baseline = Calc(logs, Period, Today);

        Assert.NotNull(baseline);
        Assert.Equal(new TimeOnly(0, 0), baseline.TypicalBedtime);
    }

    [Fact]
    public void Calculate_ReportsNoTypicalTime_WhenTimesAreScatteredAroundTheClock()
    {
        var logs = Window(RequiredDays);
        for (var i = 0; i < 8; i++)
            logs[i].SleepStartTime = logs[i].Date.ToDateTime(new TimeOnly(i * 3, 0), DateTimeKind.Utc);

        var baseline = Calc(logs, Period, Today);

        Assert.NotNull(baseline);
        Assert.Null(baseline.TypicalBedtime);
    }

    // ── Day-of-week profile ─────────────────────────────────────────────────────

    [Fact]
    public void Calculate_WritesTheWeekdayProfileMondayFirst()
    {
        var logs = Window(RequiredDays);
        foreach (var log in logs)
        {
            // Monday 1000 … Sunday 7000, so a Monday/Sunday mix-up cannot pass.
            log.Steps = log.Date.DayOfWeek switch
            {
                DayOfWeek.Monday => 1_000,
                DayOfWeek.Tuesday => 2_000,
                DayOfWeek.Wednesday => 3_000,
                DayOfWeek.Thursday => 4_000,
                DayOfWeek.Friday => 5_000,
                DayOfWeek.Saturday => 6_000,
                _ => 7_000,
            };
        }

        var baseline = Calc(logs, Period, Today);

        Assert.NotNull(baseline);
        Assert.Equal(
            new int?[] { 1_000, 2_000, 3_000, 4_000, 5_000, 6_000, 7_000 },
            JsonSerializer.Deserialize<int?[]>(baseline.StepsByDayOfWeek!));
    }

    [Fact]
    public void Calculate_LeavesAWeekdayNull_WhenItHasTooFewSamples()
    {
        var logs = Window(RequiredDays);
        var mondays = logs.Where(l => l.Date.DayOfWeek == DayOfWeek.Monday).ToList();
        foreach (var log in mondays)
            log.Steps = 4_000;

        var baseline = Calc(logs, Period, Today);

        Assert.NotNull(baseline);
        var byDayOfWeek = JsonSerializer.Deserialize<int?[]>(baseline.StepsByDayOfWeek!)!;
        Assert.Equal(4_000, byDayOfWeek[0]);
        // Null rather than zero: "no data for Tuesdays" must not read as "this member does not move".
        Assert.All(byDayOfWeek.Skip(1), value => Assert.Null(value));
    }

    [Fact]
    public void Calculate_OmitsTheWeekdayProfile_WhenNoDayHasEnoughSteps()
    {
        var baseline = Calc(Window(RequiredDays), Period, Today);

        Assert.NotNull(baseline);
        Assert.Null(baseline.StepsByDayOfWeek);
    }
}
