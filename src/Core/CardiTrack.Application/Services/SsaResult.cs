namespace CardiTrack.Application.Services;

/// <summary>
/// SSA parameters the real-time assessor and the decomposer must agree on. Kept in Application
/// so neither side takes a dependency on Math.NET to read them.
/// </summary>
public static class SsaParameters
{
    /// <summary>Default embedding window (L) — 30 samples, per the design table in llm_design.md.</summary>
    public const int DefaultWindowSize = 30;

    /// <summary>
    /// Identifies the numerical engine that produces <see cref="SsaResult"/>. Stored in docs
    /// and Art. 22 evidence, not on the assessment row — a change here re-triggers the
    /// validation protocol in <c>docs/compliance/art22_alerting_analysis.md</c> §5 V4.
    /// </summary>
    public const string Engine = "MathNet.Numerics.Evd";
}

/// <summary>
/// One series split three ways: what persists, what repeats, and what is left.
/// Noise is computed as the residual, so Trend + Oscillation + Noise reconstructs the input to
/// within floating-point rounding — nothing is invented or lost beyond rounding error.
/// </summary>
public sealed record SsaResult(double[] Trend, double[] Oscillation, double[] Noise)
{
    /// <summary>The trend's latest value — what "the level right now, denoised" means.</summary>
    public double TrendLast => Trend[^1];

    /// <summary>
    /// RMS of the noise component — the yardstick a deviation is measured against. A departure
    /// small relative to this is indistinguishable from sensor noise; one several multiples of
    /// it is not.
    /// </summary>
    public double NoiseRms
    {
        get
        {
            var sum = 0.0;
            foreach (var value in Noise)
                sum += value * value;
            return Math.Sqrt(sum / Noise.Length);
        }
    }
}
