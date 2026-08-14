using CardiTrack.Application.Interfaces.Services;
using MathNet.Numerics.Statistics;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// Math.NET Numerics implementation of <see cref="IDescriptiveStatistics"/>. Mean and sample
/// σ match <c>BaselineCalculator</c>'s formulas (arithmetic mean; n−1); median / MAD /
/// percentile are the robust alternatives the calculator does not yet persist — see
/// <c>docs/technical/mathnet_numerics.md</c>.
/// </summary>
public sealed class MathNetDescriptiveStatistics : IDescriptiveStatistics
{
    public double Mean(IReadOnlyList<double> samples)
    {
        Require(samples, minimum: 1, nameof(Mean));
        return samples.Mean();
    }

    public double SampleStandardDeviation(IReadOnlyList<double> samples)
    {
        Require(samples, minimum: 2, nameof(SampleStandardDeviation));
        return samples.StandardDeviation();
    }

    public double Median(IReadOnlyList<double> samples)
    {
        Require(samples, minimum: 1, nameof(Median));
        return samples.Median();
    }

    public double MedianAbsoluteDeviation(IReadOnlyList<double> samples)
    {
        Require(samples, minimum: 1, nameof(MedianAbsoluteDeviation));
        var median = samples.Median();
        var deviations = new double[samples.Count];
        for (var i = 0; i < samples.Count; i++)
            deviations[i] = Math.Abs(samples[i] - median);
        return deviations.Median();
    }

    public double Percentile(IReadOnlyList<double> samples, int percentile)
    {
        Require(samples, minimum: 1, nameof(Percentile));
        if (percentile is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(percentile), percentile, "Percentile must be between 0 and 100 inclusive.");
        }

        return samples.Percentile(percentile);
    }

    private static void Require(IReadOnlyList<double> samples, int minimum, string operation)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count < minimum)
        {
            throw new ArgumentException(
                $"{operation} needs at least {minimum} sample(s); got {samples.Count}.",
                nameof(samples));
        }
    }
}
