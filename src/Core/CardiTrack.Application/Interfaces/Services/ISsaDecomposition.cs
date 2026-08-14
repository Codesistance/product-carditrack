using CardiTrack.Application.Services;

namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// Singular Spectrum Analysis — the real-time path's pre-processor
/// (<c>docs/llm_design.md</c>): splits a short physiological series into trend, oscillation
/// and noise. The port lives here so Application stays package-free; the eigen-decomposition
/// is supplied by Math.NET Numerics in Infrastructure.
/// </summary>
public interface ISsaDecomposition
{
    /// <summary>
    /// Decomposes <paramref name="series"/>. Requires at least twice the window in samples —
    /// below that the embedding is too thin to separate anything, and the caller should not be
    /// assessing at all (mirrors the coverage-gate philosophy the baselines use).
    /// </summary>
    SsaResult Decompose(IReadOnlyList<double> series, int windowSize = SsaParameters.DefaultWindowSize);
}
