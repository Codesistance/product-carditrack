using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using MathNet.Numerics.LinearAlgebra;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// Singular Spectrum Analysis, the pre-processing step of the real-time path
/// (docs/llm_design.md): decomposes a short physiological series (typically 60 one-minute heart
/// rate samples) into <em>trend</em>, <em>oscillation</em> and <em>noise</em>, so the model
/// downstream reasons over a denoised level instead of raw sensor jitter.
/// <para>
/// Implemented via the lag-covariance route (Broomhead–King): embed the series into an
/// L-lagged trajectory matrix, eigen-decompose its L×L covariance with Math.NET Numerics'
/// symmetric EVD (the Application layer's zero-package invariant forbids taking that
/// dependency in Core), reconstruct one elementary series per leading eigenvector by
/// diagonal averaging, and group: the first component is trend, the next
/// <see cref="OscillationComponents"/> are oscillation, and noise is the exact residual.
/// </para>
/// <para>
/// Parameter defaults follow the design table in llm_design.md: a 30-sample window captures
/// roughly two cardiac-adjacent cycles and one activity micro-burst at 1-minute cadence, and
/// three components separate circadian drift plus short oscillation from noise.
/// </para>
/// </summary>
public sealed class SsaDecomposition : ISsaDecomposition
{
    /// <summary>Eigen-components grouped into the oscillation series (after the trend's one).</summary>
    private const int OscillationComponents = 2;

    /// <inheritdoc />
    public SsaResult Decompose(
        IReadOnlyList<double> series, int windowSize = SsaParameters.DefaultWindowSize)
    {
        ArgumentNullException.ThrowIfNull(series);
        if (windowSize < 2)
            throw new ArgumentOutOfRangeException(nameof(windowSize), windowSize, "Window must be at least 2.");
        if (series.Count < windowSize * 2)
        {
            throw new ArgumentException(
                $"SSA needs at least {windowSize * 2} samples for a window of {windowSize}; got {series.Count}.",
                nameof(series));
        }

        var n = series.Count;
        var l = windowSize;
        var k = n - l + 1;

        // Lag-covariance matrix C[i,j] = (1/K) Σ_t x[t+i] x[t+j] — L×L, symmetric.
        var covariance = new double[l, l];
        for (var i = 0; i < l; i++)
        {
            for (var j = i; j < l; j++)
            {
                var sum = 0.0;
                for (var t = 0; t < k; t++)
                    sum += series[t + i] * series[t + j];
                covariance[i, j] = sum / k;
                covariance[j, i] = covariance[i, j];
            }
        }

        var (eigenvalues, eigenvectors) = SymmetricEvd(covariance);

        // Order components by descending eigenvalue — variance captured.
        var order = Enumerable.Range(0, l).OrderByDescending(i => eigenvalues[i]).ToArray();

        var trend = ReconstructComponent(series, eigenvectors, order[0], l, k);

        var oscillation = new double[n];
        foreach (var componentIndex in order.Skip(1).Take(OscillationComponents))
        {
            var component = ReconstructComponent(series, eigenvectors, componentIndex, l, k);
            for (var t = 0; t < n; t++)
                oscillation[t] += component[t];
        }

        // The residual — never a reconstruction of the remaining components, so
        // Trend + Oscillation + Noise recovers the input to within floating-point rounding
        // (bit-exact identity is not promised: four rounded operations sit in the round trip).
        var noise = new double[n];
        for (var t = 0; t < n; t++)
            noise[t] = series[t] - trend[t] - oscillation[t];

        return new SsaResult(trend, oscillation, noise);
    }

    /// <summary>
    /// Reconstructs one elementary series: project the trajectory matrix onto eigenvector u
    /// (X_i = u uᵀ X), then hankelize by averaging each anti-diagonal back into a series.
    /// Done without materialising X_i — sums are accumulated straight into the output.
    /// </summary>
    private static double[] ReconstructComponent(
        IReadOnlyList<double> series, double[,] eigenvectors, int component, int l, int k)
    {
        var u = new double[l];
        for (var i = 0; i < l; i++)
            u[i] = eigenvectors[i, component];

        // Principal component v[t] = Σ_i u[i] x[t+i]  (length K)
        var v = new double[k];
        for (var t = 0; t < k; t++)
        {
            var sum = 0.0;
            for (var i = 0; i < l; i++)
                sum += u[i] * series[t + i];
            v[t] = sum;
        }

        // Elementary matrix entry (i,t) = u[i] * v[t]; entry (i,t) contributes to series
        // position i + t. Diagonal averaging divides by how many (i,t) pairs land there.
        var n = series.Count;
        var sums = new double[n];
        var counts = new int[n];
        for (var i = 0; i < l; i++)
        {
            for (var t = 0; t < k; t++)
            {
                sums[i + t] += u[i] * v[t];
                counts[i + t]++;
            }
        }

        var reconstructed = new double[n];
        for (var t = 0; t < n; t++)
            reconstructed[t] = sums[t] / counts[t];
        return reconstructed;
    }

    /// <summary>
    /// Symmetric eigen-decomposition of the lag-covariance. Math.NET's EVD replaces the
    /// previous cyclic-Jacobi implementation: same BK-SSA algebra, a LAPACK-quality solver.
    /// Eigenvalues of a real symmetric matrix are real; the imaginary part is discarded.
    /// </summary>
    private static (double[] Values, double[,] Vectors) SymmetricEvd(double[,] matrix)
    {
        var source = Matrix<double>.Build.DenseOfArray(matrix);
        var evd = source.Evd(Symmetricity.Symmetric);
        var l = source.RowCount;

        var values = new double[l];
        var vectors = new double[l, l];
        for (var i = 0; i < l; i++)
        {
            values[i] = evd.EigenValues[i].Real;
            for (var row = 0; row < l; row++)
                vectors[row, i] = evd.EigenVectors[row, i];
        }

        return (values, vectors);
    }
}
