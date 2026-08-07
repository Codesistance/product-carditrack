using System.Text.Json;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;

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
        Assert.Null(BaselineCalculator.Calculate(MemberId, Window(RequiredDays - 1), Period, Today));
    }

    [Fact]
    public void Calculate_ReturnsBaseline_AtExactlyTheRequiredCoverage()
    {
        var baseline = BaselineCalculator.Calculate(MemberId, Window(RequiredDays), Period, Today);

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

        Assert.Null(BaselineCalculator.Calculate(MemberId, logs, Period, Today));
    }

    [Fact]
    public void Calculate_CountsADayOnce_WhenTwoDevicesReportIt()
    {
        // 24 days of data spread over 23 dates: the duplicate must not buy the member coverage.
        var logs = Window(RequiredDays - 1);
        logs.Add(new ActivityLog { CardiMemberId = MemberId, Date = Today, RestingHeartRate = 71 });

        Assert.Null(BaselineCalculator.Calculate(MemberId, logs, Period, Today));
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
        var baseline = BaselineCalculator.Calculate(MemberId, logs, Period, Today);

        Assert.NotNull(baseline);
        var byDayOfWeek = JsonSerializer.Deserialize<int?[]>(baseline.StepsByDayOfWeek!)!;
        Assert.Contains(9_000, byDayOfWeek);
        Assert.DoesNotContain(5_000, byDayOfWeek);
    }

    [Fact]
    public void Calculate_RejectsANonPositivePeriod()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BaselineCalculator.Calculate(MemberId, Window(RequiredDays), 0, Today));
    }

    // ── Averages and spread ─────────────────────────────────────────────────────

    [Fact]
    public void Calculate_UsesTheSampleStandardDeviation()
    {
        var logs = Window(RequiredDays);
        int[] steps = [2_000, 4_000, 4_000, 4_000, 5_000, 5_000, 7_000, 9_000];
        for (var i = 0; i < steps.Length; i++)
            logs[i].Steps = steps[i];

        var baseline = BaselineCalculator.Calculate(MemberId, logs, Period, Today);

        Assert.NotNull(baseline);
        Assert.Equal(5_000, baseline.AvgSteps);
        // n−1 gives 2138.09; the population form would report 2000 and quietly narrow the
        // "normal range" the dashboard draws from it.
        Assert.Equal(2138.09m, baseline.StdDevSteps);
    }

    [Fact]
    public void Calculate_LeavesAThinlySampledMetricNull_WhileTheRestPopulate()
    {
        var logs = Window(RequiredDays);
        for (var i = 0; i < 6; i++)
            logs[i].SleepMinutes = 420;

        var baseline = BaselineCalculator.Calculate(MemberId, logs, Period, Today);

        Assert.NotNull(baseline);
        Assert.Null(baseline.AvgSleepMinutes);
        Assert.Equal(60, baseline.AvgRestingHeartRate);
    }

    [Fact]
    public void Calculate_ReportsTheObservedMaximumHeartRate_WithoutASampleFloor()
    {
        var logs = Window(RequiredDays);
        logs[0].MaxHeartRate = 142;
        logs[1].MaxHeartRate = 118;

        var baseline = BaselineCalculator.Calculate(MemberId, logs, Period, Today);

        Assert.NotNull(baseline);
        Assert.Equal(142, baseline.MaxHeartRateObserved);
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

        var baseline = BaselineCalculator.Calculate(MemberId, logs, Period, Today);

        Assert.NotNull(baseline);
        Assert.Equal(new TimeOnly(0, 0), baseline.TypicalBedtime);
    }

    [Fact]
    public void Calculate_ReportsNoTypicalTime_WhenTimesAreScatteredAroundTheClock()
    {
        var logs = Window(RequiredDays);
        for (var i = 0; i < 8; i++)
            logs[i].SleepStartTime = logs[i].Date.ToDateTime(new TimeOnly(i * 3, 0), DateTimeKind.Utc);

        var baseline = BaselineCalculator.Calculate(MemberId, logs, Period, Today);

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

        var baseline = BaselineCalculator.Calculate(MemberId, logs, Period, Today);

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

        var baseline = BaselineCalculator.Calculate(MemberId, logs, Period, Today);

        Assert.NotNull(baseline);
        var byDayOfWeek = JsonSerializer.Deserialize<int?[]>(baseline.StepsByDayOfWeek!)!;
        Assert.Equal(4_000, byDayOfWeek[0]);
        // Null rather than zero: "no data for Tuesdays" must not read as "this member does not move".
        Assert.All(byDayOfWeek.Skip(1), value => Assert.Null(value));
    }

    [Fact]
    public void Calculate_OmitsTheWeekdayProfile_WhenNoDayHasEnoughSteps()
    {
        var baseline = BaselineCalculator.Calculate(MemberId, Window(RequiredDays), Period, Today);

        Assert.NotNull(baseline);
        Assert.Null(baseline.StepsByDayOfWeek);
    }
}
