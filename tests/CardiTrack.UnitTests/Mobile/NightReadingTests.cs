using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Charts;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// How the sleep surfaces tell the three kinds of night apart (decision 2026-09-25): an awake night
/// is a real 0 that must never be printed as one, a pending night may still arrive, and a night
/// nothing reached us for stays the gap it always was.
/// </summary>
public class NightReadingTests
{
    private static readonly DateOnly Today = new(2026, 9, 25);

    private static MetricPoint Night(int daysAgo, decimal? hours, NightSleepStatus? status) =>
        new() { Date = Today.AddDays(-daysAgo), Value = hours, NightStatus = status };

    [Fact]
    public void An_awake_night_is_the_status_not_the_zero()
    {
        Assert.True(NightReading.IsAwake(Night(0, 0m, NightSleepStatus.Awake)));

        // The status is what establishes it; a zero with no status is left to the one host that
        // needs the server's convention (see WithAwakeZeros).
        Assert.False(NightReading.IsAwake(Night(0, 0m, null)));
        Assert.False(NightReading.IsAwake(Night(0, 7m, NightSleepStatus.Slept)));
    }

    [Fact]
    public void A_pending_night_has_no_figure_yet()
    {
        Assert.True(NightReading.IsPending(Night(0, null, NightSleepStatus.Pending)));
        Assert.False(NightReading.IsPending(Night(0, null, NightSleepStatus.NoData)));
        Assert.False(NightReading.IsPending(Night(0, null, null)));
    }

    [Fact]
    public void Last_night_is_pending_only_when_the_newest_point_is()
    {
        var pending = new DashboardMetric
        {
            Value = 6.8m,
            NightStatus = NightSleepStatus.Slept,
            Series = [Night(1, 6.8m, NightSleepStatus.Slept), Night(0, null, NightSleepStatus.Pending)],
        };
        var arrived = new DashboardMetric
        {
            Series = [Night(1, null, NightSleepStatus.Pending), Night(0, 7m, NightSleepStatus.Slept)],
        };

        Assert.True(NightReading.LastNightPending(pending));
        Assert.False(NightReading.LastNightPending(arrived));
        Assert.False(NightReading.LastNightPending(new DashboardMetric()));
    }

    /// <summary>
    /// Only the tail counts: a pending night is by definition the latest, and one stranded by a
    /// later reading is drawn as the gap it has become rather than as a night still on its way.
    /// </summary>
    [Fact]
    public void Only_a_trailing_run_of_pending_nights_counts()
    {
        IReadOnlyList<MetricPoint> points =
        [
            Night(3, null, NightSleepStatus.Pending),
            Night(2, 7m, NightSleepStatus.Slept),
            Night(1, null, NightSleepStatus.Pending),
            Night(0, null, NightSleepStatus.Pending),
        ];

        Assert.Equal(2, NightReading.TrailingPending(points));
        Assert.Equal(0, NightReading.TrailingPending([Night(0, 7m, NightSleepStatus.Slept)]));
        Assert.Equal(0, NightReading.TrailingPending([Night(0, null, NightSleepStatus.NoData)]));
    }

    [Fact]
    public void Unclassified_zeros_are_read_as_awake_on_a_copy()
    {
        IReadOnlyList<MetricPoint> sleep =
        [
            Night(2, 6.5m, null),
            Night(1, 0m, null),
            Night(0, null, null),
        ];

        var read = NightReading.WithAwakeZeros(sleep);

        Assert.NotSame(sleep, read);
        Assert.Equal([false, true, false], read.Select(NightReading.IsAwake));
        Assert.Null(sleep[1].NightStatus);
        Assert.Equal(sleep.Select(p => (p.Date, p.Value)), read.Select(p => (p.Date, p.Value)));
    }

    [Fact]
    public void A_series_with_no_unclassified_zero_comes_back_as_itself()
    {
        IReadOnlyList<MetricPoint> sleep = [Night(1, 6.5m, null), Night(0, 0m, NightSleepStatus.Awake)];

        Assert.Same(sleep, NightReading.WithAwakeZeros(sleep));
    }

    /// <summary>The captions are the server's own words for a pending night, not a paraphrase.</summary>
    [Fact]
    public void The_pending_captions_speak_the_servers_words()
    {
        Assert.EndsWith(ReadingFigures.PendingNight, NightReading.PendingCaption);
        Assert.EndsWith(ReadingFigures.PendingNight, NightReading.NightBeforeCaption);
        Assert.StartsWith("Night before", NightReading.NightBeforeCaption);
    }
}
