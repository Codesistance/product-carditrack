namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// Descriptive statistics over a finite sample. The port exists so Application can name the
/// operations without taking a NuGet package; Math.NET Numerics implements them in
/// Infrastructure. Baseline calculation and the R1 alert rules still use the hand-rolled
/// mean/σ in <c>BaselineCalculator</c> — switching those formulas is a product change, not
/// a wiring change (see <c>docs/technical/mathnet_numerics.md</c>).
/// </summary>
public interface IDescriptiveStatistics
{
    /// <summary>Arithmetic mean. Requires at least one sample.</summary>
    double Mean(IReadOnlyList<double> samples);

    /// <summary>Sample standard deviation (n−1). Requires at least two samples.</summary>
    double SampleStandardDeviation(IReadOnlyList<double> samples);

    /// <summary>Median (50th percentile). Requires at least one sample.</summary>
    double Median(IReadOnlyList<double> samples);

    /// <summary>
    /// Median absolute deviation from the median, unscaled (median of |x − median|).
    /// Requires at least one sample.
    /// </summary>
    double MedianAbsoluteDeviation(IReadOnlyList<double> samples);

    /// <summary>
    /// Sample percentile in <c>[0, 100]</c> using Math.NET's interpolation. Requires at least
    /// one sample.
    /// </summary>
    double Percentile(IReadOnlyList<double> samples, int percentile);
}
