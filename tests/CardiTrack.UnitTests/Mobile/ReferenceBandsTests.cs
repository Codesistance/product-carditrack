using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Charts;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// Which published range a chart may draw, and what its key calls it.
/// </summary>
public class ReferenceBandsTests
{
    private static MetricReference Who(bool normal = false) =>
        new() { Low = 12m, High = 20m, Source = "WHO", IsPublishedNormal = normal };

    /// <summary>
    /// WHO's 12–20 is a rate at rest, and breathing measured across hours of sleep is not that
    /// measurement — by either name the two drawing surfaces use for it.
    /// </summary>
    [Theory]
    [InlineData("overnightBreathingRate")]
    [InlineData("Breathing while asleep")]
    public void Breathing_while_asleep_never_gets_a_band(string metric)
    {
        Assert.Null(ReferenceBands.Drawable(metric, Who()));
    }

    [Theory]
    [InlineData("sleep")]
    [InlineData("spo2")]
    [InlineData("Resting heart rate")]
    public void Every_other_metric_keeps_the_band_it_was_sent(string metric)
    {
        var band = Who();

        Assert.Same(band, ReferenceBands.Drawable(metric, band));
        Assert.Null(ReferenceBands.Drawable(metric, null));
    }

    [Fact]
    public void The_key_calls_a_range_normal_only_where_it_is_the_normal()
    {
        Assert.Equal("Normal", ReferenceBands.Noun(Who(normal: true)));
        Assert.Equal("Typical", ReferenceBands.Noun(Who()));
    }
}
