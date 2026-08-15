using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Charts;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// The star-column widths behind the stacked Activity bar. The contract these all defend is that
/// no width is ever negative or NaN: the widths become <c>GridLength</c>s, which reject both, and
/// that throw happens while the dashboard is applying its metrics — so it does not spoil a bar,
/// it takes the whole screen down and leaves it on loading placeholders.
/// </summary>
public class StackedTrackTests
{
    private static readonly DateOnly Today = new(2026, 8, 15);

    [Fact]
    public void Behind_yesterday_fills_once_and_leaves_the_rest_empty()
    {
        var track = StackedTrack.For(0.4, 0);

        Assert.Equal(0.4, track.Compared, 10);
        Assert.Equal(0d, track.Overflow);
        Assert.Equal(0.6, track.Remainder, 10);
        Assert.False(track.IsStacked);
    }

    [Fact]
    public void Matching_yesterday_fills_the_track_with_no_second_colour()
    {
        var track = StackedTrack.For(1, 0);

        Assert.Equal(1d, track.Compared);
        Assert.Equal(0d, track.Overflow);
        Assert.Equal(0d, track.Remainder);
        Assert.False(track.IsStacked);
    }

    [Fact]
    public void Ahead_of_yesterday_stacks_the_extra_and_leaves_no_remainder()
    {
        var track = StackedTrack.For(5000d / 6200d, 1200d / 6200d);

        Assert.Equal(5000d / 6200d, track.Compared, 10);
        Assert.Equal(1200d / 6200d, track.Overflow, 10);
        Assert.Equal(0d, track.Remainder, 10);
        Assert.True(track.IsStacked);
    }

    [Fact]
    public void A_zero_yesterday_gives_the_whole_track_to_the_extra()
    {
        var track = StackedTrack.For(0, 1);

        Assert.Equal(0d, track.Compared);
        Assert.Equal(1d, track.Overflow);
        Assert.Equal(0d, track.Remainder);
        Assert.True(track.IsStacked);
    }

    /// <summary>
    /// The regression. Compared and Overflow reach the track as two independently rounded
    /// decimal-to-double conversions of shares that sum to exactly 1. For these pairs the double
    /// sum rounds back to exactly 1.0 — so a "&gt; 1" guard never fires — while
    /// <c>1 - compared - overflow</c> lands a few ulps below zero. Every one of these threw
    /// <c>ArgumentException: value is less than 0 or is not a number</c> and killed the dashboard.
    /// </summary>
    [Theory]
    [InlineData(5, 4)]
    [InlineData(6, 5)]
    [InlineData(10, 8)]
    [InlineData(10, 9)]
    [InlineData(11, 2)]
    [InlineData(11, 9)]
    [InlineData(12, 5)]
    [InlineData(12, 7)]
    [InlineData(13, 3)]
    [InlineData(13, 10)]
    public void Rounding_never_drives_a_width_below_zero(int todaySteps, int yesterdaySteps)
    {
        var progress = ActivityDayProgress.For(
            Series((Today.AddDays(-1), yesterdaySteps), (Today, todaySteps)));
        Assert.NotNull(progress);

        var track = StackedTrack.For(progress.Value.Compared, progress.Value.Overflow);

        AssertDrawable(track);
        Assert.True(track.IsStacked, "today is ahead, so the extra must be drawn");
    }

    /// <summary>
    /// The same guarantee over the whole small-count space, where the rounding residue is most
    /// likely — a caregiver's relative on a quiet day is exactly who these values belong to.
    /// </summary>
    [Fact]
    public void No_pairing_of_days_can_produce_an_undrawable_track()
    {
        for (var today = 1; today <= 300; today++)
        {
            for (var yesterday = 1; yesterday <= 300; yesterday++)
            {
                var progress = ActivityDayProgress.For(
                    Series((Today.AddDays(-1), yesterday), (Today, today)));
                Assert.NotNull(progress);

                AssertDrawable(StackedTrack.For(progress.Value.Compared, progress.Value.Overflow));
            }
        }
    }

    [Fact]
    public void An_over_full_track_gives_way_on_the_extra_rather_than_overflowing()
    {
        var track = StackedTrack.For(0.8, 0.9);

        Assert.Equal(0.8, track.Compared, 10);
        Assert.Equal(0.2, track.Overflow, 10);
        Assert.Equal(0d, track.Remainder, 10);
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(0.5, double.NaN)]
    [InlineData(double.NaN, double.NaN)]
    [InlineData(-1, -1)]
    [InlineData(2, 2)]
    [InlineData(double.PositiveInfinity, double.NegativeInfinity)]
    public void Nonsense_input_still_yields_a_drawable_track(double compared, double overflow)
    {
        // Nothing feeds these today, and the point is that nothing has to: the widths are one
        // GridLength away from taking the dashboard down, so this type owes its caller a drawable
        // answer for any input rather than a promise about its callers.
        AssertDrawable(StackedTrack.For(compared, overflow));
    }

    /// <summary>The invariant every caller depends on, in the form GridLength enforces it.</summary>
    private static void AssertDrawable(StackedTrack track)
    {
        foreach (var (name, width) in new[]
                 {
                     (nameof(track.Compared), track.Compared),
                     (nameof(track.Overflow), track.Overflow),
                     (nameof(track.Remainder), track.Remainder),
                 })
        {
            Assert.False(double.IsNaN(width), $"{name} is NaN");
            Assert.True(width >= 0, $"{name} is negative ({width:R})");
        }
    }

    private static List<MetricPoint> Series(params (DateOnly Date, decimal? Value)[] points) =>
        points.Select(p => new MetricPoint { Date = p.Date, Value = p.Value }).ToList();
}
