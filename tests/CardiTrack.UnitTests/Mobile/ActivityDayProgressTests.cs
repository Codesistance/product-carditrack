using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Charts;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// The Activity Key Metrics bar fills against the previous calendar day, and its max grows to
/// today only once today is strictly ahead. Beating yesterday is a second stacked colour so it
/// is not indistinguishable from matching it.
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
        Assert.Equal(0.4, progress.Value.Compared, 6);
        Assert.Equal(0d, progress.Value.Overflow);
        Assert.True(progress.Value.PreviousIsYesterday);
        Assert.Equal($"vs {5000m:N0} yesterday", progress.Value.Caption);
        Assert.Equal($"{2000m:N0} steps vs {5000m:N0} yesterday", progress.Value.Description);
    }

    [Fact]
    public void Matching_yesterday_fills_the_bar_without_growing_the_max()
    {
        var progress = ActivityDayProgress.For(Series((Today.AddDays(-1), 5000m), (Today, 5000m)));

        Assert.NotNull(progress);
        Assert.Equal(1d, progress.Value.Fill);
        Assert.Equal(1d, progress.Value.Compared);
        Assert.Equal(0d, progress.Value.Overflow);
        Assert.Equal(5000m, progress.Value.Previous);
        Assert.Equal($"{5000m:N0} steps vs {5000m:N0} yesterday", progress.Value.Description);
    }

    [Fact]
    public void Exceeding_yesterday_grows_the_max_to_today_and_stays_full()
    {
        // 6,200 / 6,200, not 6,200 / 5,000 clamped. Growing the max is what keeps the extra
        // visible as a second stacked colour instead of painting past 100% or looking like a match.
        var progress = ActivityDayProgress.For(Series((Today.AddDays(-1), 5000m), (Today, 6200m)));

        Assert.NotNull(progress);
        Assert.Equal(1d, progress.Value.Fill);
        Assert.Equal(5000d / 6200d, progress.Value.Compared, 6);
        Assert.Equal(1200d / 6200d, progress.Value.Overflow, 6);
        Assert.Equal(6200m, progress.Value.Current);
        Assert.Equal(5000m, progress.Value.Previous);
        Assert.Equal($"{6200m:N0} steps vs {5000m:N0} yesterday", progress.Value.Description);
    }

    [Fact]
    public void A_zero_yesterday_and_any_steps_today_is_already_ahead()
    {
        var progress = ActivityDayProgress.For(Series((Today.AddDays(-1), 0m), (Today, 400m)));

        Assert.NotNull(progress);
        Assert.Equal(1d, progress.Value.Fill);
        Assert.Equal(0d, progress.Value.Compared);
        Assert.Equal(1d, progress.Value.Overflow);
        Assert.Equal($"vs {0m:N0} yesterday", progress.Value.Caption);
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
        Assert.Equal($"vs previous day's {4000m:N0}", progress.Value.Caption);
        Assert.Equal($"{3000m:N0} steps vs {4000m:N0} the previous day", progress.Value.Description);
    }

    [Fact]
    public void An_explicit_zero_today_is_an_empty_bar_against_yesterday()
    {
        // A provider that has already written today's row as 0 is not "no steps yet" — that is
        // a null, above. Zero is a reading: they have been counted and have not moved. Falling
        // back to yesterday would hide that, and would put yesterday's total on a bar labelled
        // as today.
        var progress = ActivityDayProgress.For(Series((Today.AddDays(-1), 5000m), (Today, 0m)));

        Assert.NotNull(progress);
        Assert.Equal(0m, progress.Value.Current);
        Assert.Equal(5000m, progress.Value.Previous);
        Assert.Equal(0d, progress.Value.Fill);
        Assert.Equal(0d, progress.Value.Compared);
        Assert.Equal(0d, progress.Value.Overflow);
        Assert.True(progress.Value.PreviousIsYesterday);
        Assert.Equal($"vs {5000m:N0} yesterday", progress.Value.Caption);
        Assert.Equal($"{0m:N0} steps vs {5000m:N0} yesterday", progress.Value.Description);
    }

    [Fact]
    public void A_finished_day_that_beat_the_day_before_is_full()
    {
        var progress = ActivityDayProgress.For(Series(
            (Today.AddDays(-2), 4000m), (Today.AddDays(-1), 5500m), (Today, null)));

        Assert.NotNull(progress);
        Assert.Equal(1d, progress.Value.Fill);
        Assert.Equal(4000d / 5500d, progress.Value.Compared, 6);
        Assert.Equal(1500d / 5500d, progress.Value.Overflow, 6);
        Assert.Equal($"{5500m:N0} steps vs {4000m:N0} the previous day", progress.Value.Description);
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

    [Fact]
    public void The_change_against_the_previous_day_is_what_the_card_shows_beside_the_reading()
    {
        var ahead = ActivityDayProgress.For(Series((Today.AddDays(-1), 5959m), (Today, 8846m)));
        var behind = ActivityDayProgress.For(Series((Today.AddDays(-1), 5000m), (Today, 2000m)));
        var level = ActivityDayProgress.For(Series((Today.AddDays(-1), 5000m), (Today, 5000m)));

        Assert.NotNull(ahead);
        Assert.NotNull(behind);
        Assert.NotNull(level);
        Assert.Equal(48.4m, ahead.Value.ChangePercent);
        Assert.Equal(-60m, behind.Value.ChangePercent);
        Assert.Equal(0m, level.Value.ChangePercent);
    }

    [Fact]
    public void A_previous_day_of_zero_has_no_percentage_to_state()
    {
        // The bar still draws — today is the whole of it — but "up ∞%" is not a reading.
        var progress = ActivityDayProgress.For(Series((Today.AddDays(-1), 0m), (Today, 4000m)));

        Assert.NotNull(progress);
        Assert.Null(progress.Value.ChangePercent);
    }

    private static List<MetricPoint> Series(params (DateOnly Date, decimal? Value)[] points) =>
        points.Select(p => new MetricPoint { Date = p.Date, Value = p.Value }).ToList();
}
