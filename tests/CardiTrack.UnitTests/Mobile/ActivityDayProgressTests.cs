using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Charts;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// The Activity Key Metrics bar fills against the previous calendar day, and its max grows to
/// today only once today is strictly ahead — one fill, not an overflow colour.
/// </summary>
public class ActivityDayProgressTests
{
    private static readonly DateOnly Today = new(2026, 8, 15);

    [Fact]
    public void Today_behind_yesterday_fills_against_yesterday()
    {
        var progress = ActivityDayProgress.For(Series((Today.AddDays(-1), 5000m), (Today, 2000m)));

        Assert.NotNull(progress);
        Assert.Equal(2000m, progress.Value.Current);
        Assert.Equal(5000m, progress.Value.Previous);
        Assert.Equal(0.4, progress.Value.Fill, 6);
        Assert.True(progress.Value.PreviousIsYesterday);
        Assert.Equal($"Yesterday {5000m:N0}", progress.Value.Caption);
        Assert.Equal($"{2000m:N0} of yesterday's {5000m:N0} steps", progress.Value.Description);
    }

    [Fact]
    public void Matching_yesterday_fills_the_bar_without_growing_the_max()
    {
        var progress = ActivityDayProgress.For(Series((Today.AddDays(-1), 5000m), (Today, 5000m)));

        Assert.NotNull(progress);
        Assert.Equal(1d, progress.Value.Fill);
        Assert.Equal(5000m, progress.Value.Previous);
        Assert.Equal($"{5000m:N0} steps, matching yesterday", progress.Value.Description);
    }

    [Fact]
    public void Exceeding_yesterday_grows_the_max_to_today_and_stays_full()
    {
        // 6,200 / 6,200, not 6,200 / 5,000 clamped. Growing the max is what keeps the bar one
        // colour instead of painting the extra 1,200 as a second target.
        var progress = ActivityDayProgress.For(Series((Today.AddDays(-1), 5000m), (Today, 6200m)));

        Assert.NotNull(progress);
        Assert.Equal(1d, progress.Value.Fill);
        Assert.Equal(6200m, progress.Value.Current);
        Assert.Equal(5000m, progress.Value.Previous);
        Assert.Equal($"{6200m:N0} steps, more than yesterday's {5000m:N0}", progress.Value.Description);
    }

    [Fact]
    public void A_zero_yesterday_and_any_steps_today_is_already_ahead()
    {
        var progress = ActivityDayProgress.For(Series((Today.AddDays(-1), 0m), (Today, 400m)));

        Assert.NotNull(progress);
        Assert.Equal(1d, progress.Value.Fill);
        Assert.Equal($"Yesterday {0m:N0}", progress.Value.Caption);
    }

    [Fact]
    public void Both_days_at_zero_have_no_bar()
    {
        Assert.Null(ActivityDayProgress.For(Series((Today.AddDays(-1), 0m), (Today, 0m))));
    }

    [Fact]
    public void A_gap_on_the_previous_calendar_day_hides_the_bar()
    {
        // Day n−2 has a total, day n−1 does not. Skipping back would label an older day as
        // yesterday.
        Assert.Null(ActivityDayProgress.For(Series((Today.AddDays(-2), 8000m), (Today.AddDays(-1), null), (Today, 1200m))));
    }

    [Fact]
    public void No_steps_yet_today_compares_the_last_finished_day_to_the_day_before_it()
    {
        var progress = ActivityDayProgress.For(Series(
            (Today.AddDays(-2), 4000m), (Today.AddDays(-1), 3000m), (Today, null)));

        Assert.NotNull(progress);
        Assert.Equal(3000m, progress.Value.Current);
        Assert.Equal(4000m, progress.Value.Previous);
        Assert.Equal(0.75, progress.Value.Fill, 6);
        Assert.False(progress.Value.PreviousIsYesterday);
        Assert.Equal($"Previous day {4000m:N0}", progress.Value.Caption);
        Assert.Equal($"{3000m:N0} of the previous day's {4000m:N0} steps", progress.Value.Description);
    }

    [Fact]
    public void A_finished_day_that_beat_the_day_before_is_full()
    {
        var progress = ActivityDayProgress.For(Series(
            (Today.AddDays(-2), 4000m), (Today.AddDays(-1), 5500m), (Today, null)));

        Assert.NotNull(progress);
        Assert.Equal(1d, progress.Value.Fill);
        Assert.Equal($"{5500m:N0} steps, more than the previous day's {4000m:N0}", progress.Value.Description);
    }

    [Fact]
    public void An_empty_series_or_a_series_with_no_values_has_no_bar()
    {
        Assert.Null(ActivityDayProgress.For(Array.Empty<MetricPoint>()));
        Assert.Null(ActivityDayProgress.For(Series((Today, null))));
    }

    [Fact]
    public void The_first_day_in_the_window_has_no_previous_day_to_fill_against()
    {
        Assert.Null(ActivityDayProgress.For(Series((Today, 1200m))));
    }

    [Fact]
    public void For_a_metric_reads_the_series_not_the_goal()
    {
        // A usual-day goal must not become the track max — that is the fitness metaphor this
        // replaced. Yesterday is 5,000; the baseline is 10,000; the bar follows yesterday.
        var metric = new DashboardMetric
        {
            Value = 2000m,
            Goal = 10000m,
            Series = Series((Today.AddDays(-1), 5000m), (Today, 2000m)),
        };

        var progress = ActivityDayProgress.For(metric);

        Assert.NotNull(progress);
        Assert.Equal(0.4, progress.Value.Fill, 6);
        Assert.Equal(5000m, progress.Value.Previous);
    }

    private static List<MetricPoint> Series(params (DateOnly Date, decimal? Value)[] points) =>
        points.Select(p => new MetricPoint { Date = p.Date, Value = p.Value }).ToList();
}
