using CardiTrack.Mobile.Core.Charts;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// The vertical extent a Key Metric trend chart plots over, once this member's own baseline and
/// the published reference range have been fitted around the readings — and the rule for when
/// they are refused the room, which is the part a caregiver would notice going wrong.
/// </summary>
public class TrendScaleTests
{
    private static TrendScale For(
        double dataMin, double dataMax, double? baseline = null,
        double? referenceLow = null, double? referenceHigh = null) =>
        TrendScale.For(dataMin, dataMax, baseline, referenceLow, referenceHigh);

    [Fact]
    public void Readings_alone_set_the_extent()
    {
        var scale = For(66, 74);

        Assert.Equal(66, scale.Min);
        Assert.Equal(74, scale.Max);
        Assert.True(scale.HasExtent);
    }

    [Fact]
    public void A_baseline_outside_the_readings_widens_the_plot_to_reach_it()
    {
        // A fortnight spent below the member's own normal: the baseline is the whole point of the
        // chart, so the plot makes room for it rather than leaving the rule off the top.
        var scale = For(4000, 4800, baseline: 5200);

        Assert.Equal(4000, scale.Min);
        Assert.Equal(5200, scale.Max);
        Assert.True(scale.Contains(5200));
    }

    [Fact]
    public void A_baseline_inside_the_readings_changes_nothing()
    {
        var scale = For(60, 80, baseline: 70);

        Assert.Equal(60, scale.Min);
        Assert.Equal(80, scale.Max);
    }

    [Fact]
    public void A_reference_range_around_the_readings_is_admitted()
    {
        // Nights of 6.2–8.4 hours against the 7–9 recommendation: the band barely stretches the
        // plot, and where the member sits inside it is the comparison worth drawing.
        var scale = For(6.2, 8.4, referenceLow: 7, referenceHigh: 9);

        Assert.Equal(6.2, scale.Min);
        Assert.Equal(9, scale.Max);
    }

    [Fact]
    public void A_reference_range_that_would_flatten_the_readings_is_refused()
    {
        // A resting heart rate that held inside two beats all month, against 60–100: admitting the
        // band would leave the line straight, so the readings keep the scale and the band is left
        // to draw clipped against the plot edges.
        var scale = For(68, 70, referenceLow: 60, referenceHigh: 100);

        Assert.Equal(68, scale.Min);
        Assert.Equal(70, scale.Max);
        Assert.False(scale.Contains(60));
        Assert.False(scale.Contains(100));
    }

    [Fact]
    public void A_reference_range_is_admitted_for_a_heart_rate_that_actually_moves()
    {
        // The case the headroom is set for: 10 bpm of movement is a readable trend even once the
        // full 60–100 band has claimed the axis.
        var scale = For(62, 72, referenceLow: 60, referenceHigh: 100);

        Assert.Equal(60, scale.Min);
        Assert.Equal(100, scale.Max);
    }

    [Fact]
    public void A_baseline_that_would_flatten_the_readings_is_refused_too()
    {
        // Weeks of near-total inactivity against a 9 000-step normal. Drawing that rule would cost
        // the shape of the readings entirely; the card names the number in its legend instead.
        var scale = For(200, 400, baseline: 9000);

        Assert.Equal(200, scale.Min);
        Assert.Equal(400, scale.Max);
        Assert.False(scale.Contains(9000));
    }

    [Fact]
    public void A_flat_window_takes_whatever_it_can_be_widened_by()
    {
        // Nothing to swamp: without the band this window has no extent at all and draws down the
        // middle of an unlabelled plot, where inside the band it shows where 96% actually sits.
        var scale = For(96, 96, referenceLow: 94, referenceHigh: 100);

        Assert.Equal(94, scale.Min);
        Assert.Equal(100, scale.Max);
        Assert.True(scale.HasExtent);
    }

    [Fact]
    public void A_flat_window_with_nothing_to_widen_it_has_no_extent()
    {
        var scale = For(96, 96);

        Assert.False(scale.HasExtent);
    }

    [Fact]
    public void A_baseline_that_has_already_widened_the_plot_cannot_let_a_band_in_behind_it()
    {
        // Both candidates are measured against the readings' own extent (2), not against the
        // running total — otherwise each admission would fund the next and the readings would
        // flatten by increments.
        var scale = For(20, 22, baseline: 30, referenceLow: 12, referenceHigh: 100);

        Assert.Equal(20, scale.Min);
        Assert.Equal(30, scale.Max);
    }
}
