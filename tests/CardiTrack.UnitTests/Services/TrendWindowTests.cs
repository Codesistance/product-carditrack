using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The spans each horizon's figures are drawn over. What these pin is the thing a heading cannot
/// enforce on its own: a narrative headed "the week just gone" has to be written from the week
/// just gone, not from a four-week slope that happens to end in it.
/// </summary>
public class TrendWindowTests
{
    private static readonly DateOnly Through = new(2026, 9, 20);
    private readonly Guid _memberId = Guid.NewGuid();

    [Fact]
    public void TheRollingPresetIsWhatTheDailyPassAlwaysUsed()
    {
        // The rolling narrative must not have changed shape when the horizons arrived. These are
        // the four constants the calculator carried before the preset existed.
        var rolling = TrendWindow.For(TrendHorizon.Rolling);

        Assert.Equal(TrendFeatureCalculator.MinimumDaysForTrend, rolling.MinimumDaysOfHistory);
        Assert.Equal(TrendFeatureCalculator.MovingAverageDays, rolling.AverageDays);
        Assert.Equal(TrendFeatureCalculator.SlopeWindowDays, rolling.SlopeDays);
        Assert.Equal(TrendFeatureCalculator.MinimumDaysForSlope, rolling.MinimumDaysForSlope);
    }

    [Fact]
    public void AnUnrecognisedHorizonFallsBackToRolling()
    {
        Assert.Same(TrendWindow.Rolling, TrendWindow.For((TrendHorizon)99));
    }

    [Fact]
    public void AWeekIsAlwaysSevenDays()
    {
        var weekly = TrendWindow.For(TrendHorizon.Weekly);

        Assert.Equal(7, weekly.AverageDays);
        Assert.Equal(7, weekly.SlopeDays);
    }

    [Theory]
    [InlineData(28)]
    [InlineData(29)]
    [InlineData(30)]
    [InlineData(31)]
    public void AMonthSpansItsOwnLength(int dayCount)
    {
        // The window ends on the month's last day, so its length is the only thing deciding
        // whether it starts on the first. A fixed thirty would have had February describing two
        // days of January, and a thirty-one-day month losing its first — while the narrative says
        // "the month that has just ended" either way.
        var monthly = TrendWindow.ForMonth(dayCount);

        Assert.Equal(dayCount, monthly.AverageDays);
        Assert.Equal(dayCount, monthly.SlopeDays);
        // The coverage bar does not move with the month's length: fourteen days is half of any of
        // them, and it is the Monthbook's own guard.
        Assert.Equal(14, monthly.MinimumDaysForSlope);
        Assert.Equal(TrendFeatureCalculator.MinimumDaysForTrend, monthly.MinimumDaysOfHistory);
    }

    [Fact]
    public void AMonthOfNoDaysIsNotAMonth()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TrendWindow.ForMonth(0));
    }

    [Fact]
    public void AFebruaryWindowDoesNotReachBackIntoJanuary()
    {
        // 2026-02-28 is the last day of a 28-day February. At a fixed thirty the average would
        // have taken in 30 and 31 January; at the month's own length it starts on the 1st.
        var through = new DateOnly(2026, 2, 28);
        var logs = Enumerable.Range(0, 60)
            .Select(offset => new ActivityLog
            {
                CardiMemberId = _memberId,
                Date = through.AddDays(-(59 - offset)),
                // January days read 10,000; February days read 2,000. A window that reaches into
                // January cannot average 2,000.
                Steps = through.AddDays(-(59 - offset)).Month == 1 ? 10_000 : 2_000,
            })
            .ToList();

        var february = TrendFeatureCalculator
            .Compute(logs, [], through, TrendWindow.ForMonth(28))!
            .Features.Single(f => f.Metric == "Steps");

        Assert.Equal(2_000m, february.RecentAverage);
        Assert.Equal(28, february.MeasuredDays);
    }

    [Fact]
    public void EachJournalHorizonAsksForTheCoverageItsBookAsksFor()
    {
        // Four of seven and fourteen of a month — the Weekbook's and Monthbook's own guards. A
        // period measured on fewer days than that is an unmeasured period whichever surface is
        // describing it.
        Assert.Equal(4, TrendWindow.For(TrendHorizon.Weekly).MinimumDaysForSlope);
        Assert.Equal(14, TrendWindow.For(TrendHorizon.Monthly).MinimumDaysForSlope);
    }

    [Fact]
    public void EveryHorizonStillNeedsAMonthOfHistoryBeforeItSaysAnything()
    {
        // The horizon changes what is described, never how much history it takes before anything
        // is. A week read against three weeks of history has no learned normal to be unusual
        // relative to.
        foreach (var horizon in Enum.GetValues<TrendHorizon>())
        {
            Assert.Equal(
                TrendFeatureCalculator.MinimumDaysForTrend,
                TrendWindow.For(horizon).MinimumDaysOfHistory);
        }

        var tooShort = Days(TrendFeatureCalculator.MinimumDaysForTrend - 1, _ => 5000);
        Assert.Null(TrendFeatureCalculator.Compute(tooShort, [], Through, TrendWindow.Weekly));
    }

    [Fact]
    public void TheWeeklyAverageReadsTheWeek_AsTheRollingOneAlsoDoes()
    {
        // Ninety days at 2,000 steps, then the final week at 10,000. Both windows average seven
        // days, so both land on the week — the control for the slope case below.
        var logs = Days(90, offset => offset >= 83 ? 10_000 : 2_000);

        Assert.Equal(10_000m, StepsFeature(logs, TrendWindow.Rolling).RecentAverage);
        Assert.Equal(10_000m, StepsFeature(logs, TrendWindow.Weekly).RecentAverage);
    }

    [Fact]
    public void TheMonthlyAverageReadsTheMonth_NotTheLastWeekOfIt()
    {
        // The same series: a month whose last week was busy is not a busy month, and the monthly
        // figure has to say so. Twenty-three days at 2,000 and seven at 10,000 averages ~3,867.
        var logs = Days(90, offset => offset >= 83 ? 10_000 : 2_000);

        var monthly = StepsFeature(logs, TrendWindow.Monthly).RecentAverage;

        Assert.NotNull(monthly);
        Assert.InRange(monthly!.Value, 3_800m, 3_900m);
    }

    [Fact]
    public void AWeeklySlopeIsFittedThroughTheWeekAlone()
    {
        // Flat for eleven weeks, then climbing 500 steps a day through the last seven. The weekly
        // slope sees only the climb; the rolling slope averages it against three flat weeks and
        // reads far shallower. A heading that says "this week" must not be carrying the latter.
        var logs = Days(90, offset => offset >= 83 ? 2_000 + ((offset - 83) * 500) : 2_000);

        var weekly = StepsFeature(logs, TrendWindow.Weekly).ChangePerDay;
        var rolling = StepsFeature(logs, TrendWindow.Rolling).ChangePerDay;

        Assert.Equal(500m, weekly);
        Assert.NotNull(rolling);
        Assert.True(rolling < weekly, "a four-week slope must flatten a climb confined to one week.");
    }

    [Fact]
    public void AWeekMeasuredOnTooFewDaysFitsNoLine()
    {
        // Ninety days of history, but only three of the final seven measured. The history gate
        // passes — the member has a learned normal — and the slope still declines, because three
        // points across a week is not a week's trajectory.
        var logs = Days(90, offset => offset < 83 ? 2_000 : offset % 3 == 0 ? 2_000 : null);

        Assert.Null(StepsFeature(logs, TrendWindow.Weekly).ChangePerDay);
    }

    [Fact]
    public void CountMeasuredDaysCountsDaysRatherThanRows()
    {
        // A member wearing two watches has two rows for one day. Counting rows would let four
        // days of two-device readings clear a bar meant to mean seven.
        var day = Through.AddDays(-1);
        var logs = new List<ActivityLog>
        {
            new() { CardiMemberId = _memberId, Date = day, Steps = 4_000 },
            new() { CardiMemberId = _memberId, Date = day, Steps = 4_100 },
            new() { CardiMemberId = _memberId, Date = Through, Steps = 5_000 },
        };

        Assert.Equal(2, TrendFeatureCalculator.CountMeasuredDays(logs));
    }

    [Fact]
    public void CountMeasuredDaysAsksTheSameRowTheFeaturesWillRead()
    {
        // Two devices reported the same day. The later row is the one Compute keeps, and it holds
        // nothing this calculator reads — so the day is not a measured day. Counting it because
        // the earlier row had steps would pass a period into a read that then finds nothing
        // there, which is exactly the disagreement the coverage check exists to prevent.
        var day = Through.AddDays(-1);
        var logs = new List<ActivityLog>
        {
            new()
            {
                CardiMemberId = _memberId, Date = day, Steps = 4_000,
                CreatedDate = new DateTime(2026, 9, 19, 1, 0, 0, DateTimeKind.Utc),
            },
            new()
            {
                CardiMemberId = _memberId, Date = day, Distance = 2.4m,
                CreatedDate = new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc),
            },
        };

        Assert.Equal(0, TrendFeatureCalculator.CountMeasuredDays(logs));
        Assert.Null(TrendFeatureCalculator.Compute(logs, [], Through, TrendWindow.Weekly));
    }

    [Fact]
    public void CountMeasuredDaysIgnoresADayCarryingNothingItReads()
    {
        // A row holding only distance and SpO2 is a row, not a measured day — the same rule the
        // cold-start gate applies.
        var logs = new List<ActivityLog>
        {
            new() { CardiMemberId = _memberId, Date = Through, Distance = 3.2m, SpO2Average = 96m },
            new() { CardiMemberId = _memberId, Date = Through.AddDays(-1), Steps = 5_000 },
        };

        Assert.Equal(1, TrendFeatureCalculator.CountMeasuredDays(logs));
    }

    [Fact]
    public void TheRenderedLinesNameTheWindowTheyWereDrawnOver()
    {
        // The label and the arithmetic come from the same object, so a weekly average cannot be
        // printed as a monthly one — which is the whole reason the window rides on the result.
        var logs = Days(90, _ => 4_000);

        var weekly = TrendFeatureCalculator.Render(
            TrendFeatureCalculator.Compute(logs, [], Through, TrendWindow.Weekly)!);
        var monthly = TrendFeatureCalculator.Render(
            TrendFeatureCalculator.Compute(logs, [], Through, TrendWindow.Monthly)!);

        Assert.Contains("last 7 days averaged", weekly);
        Assert.Contains("last 30 days averaged", monthly);
    }

    private TrendFeature StepsFeature(IReadOnlyList<ActivityLog> logs, TrendWindow window) =>
        TrendFeatureCalculator.Compute(logs, [], Through, window)!.Features.Single(f => f.Metric == "Steps");

    private List<ActivityLog> Days(int count, Func<int, int?> steps) =>
        Enumerable.Range(0, count)
            .Select(offset => new ActivityLog
            {
                CardiMemberId = _memberId,
                Date = Through.AddDays(-(count - 1 - offset)),
                Steps = steps(offset),
            })
            .ToList();
}
