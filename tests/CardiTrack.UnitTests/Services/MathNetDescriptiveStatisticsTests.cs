using CardiTrack.Infrastructure.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins Math.NET's descriptive-stat formulas against the ones <c>BaselineCalculator</c>
/// already ships (mean, sample σ) and records the robust alternatives (median, MAD) that
/// would stop a single outlier day from widening a member's "normal" — the gap documented
/// in <c>docs/technical/mathnet_numerics.md</c>, not yet wired into stored baselines.
/// </summary>
public class MathNetDescriptiveStatisticsTests
{
    private static readonly MathNetDescriptiveStatistics Stats = new();

    [Fact]
    public void Mean_MatchesTheArithmeticAverageBaselineCalculatorUses()
    {
        double[] samples = [5000, 5200, 4800, 5100];

        Assert.Equal(samples.Average(), Stats.Mean(samples));
    }

    [Fact]
    public void SampleStandardDeviation_UsesNMinusOne_MatchingBaselineCalculator()
    {
        double[] samples = [60, 62, 61, 70];
        var mean = samples.Average();
        var expected = Math.Sqrt(samples.Sum(v => (v - mean) * (v - mean)) / (samples.Length - 1));

        Assert.Equal(expected, Stats.SampleStandardDeviation(samples), precision: 10);
    }

    [Fact]
    public void AnOutlierDay_InflatesMeanAndSigma_ButBarelyMovesMedianAndMad()
    {
        // 29 ordinary days around 5_000 steps, then one 20_000-step day — a wedding, not a
        // new normal. Mean/σ (what we persist today) shift; median/MAD (what Math.NET offers)
        // stay on the ordinary cluster.
        var ordinary = Enumerable.Repeat(5000.0, 29).ToArray();
        var withOutlier = ordinary.Append(20_000.0).ToArray();

        Assert.True(Stats.Mean(withOutlier) - Stats.Mean(ordinary) > 400,
            "the outlier must pull the mean — that is the poisoning the robust stats exist to avoid");
        Assert.Equal(0.0, Stats.SampleStandardDeviation(ordinary), precision: 10);
        Assert.True(Stats.SampleStandardDeviation(withOutlier) > 1000,
            "the same day must inflate σ, widening the band that later alerts are judged against");

        Assert.Equal(5000.0, Stats.Median(withOutlier));
        Assert.Equal(0.0, Stats.MedianAbsoluteDeviation(withOutlier));
    }

    [Fact]
    public void Percentile_RefusesAPercentileOutsideZeroToOneHundred()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Stats.Percentile([1.0], 101));
    }

    [Fact]
    public void SampleStandardDeviation_RefusesASingleSample()
    {
        Assert.Throws<ArgumentException>(() => Stats.SampleStandardDeviation([1.0]));
    }
}
