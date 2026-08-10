namespace CardiTrack.Application.Services;

/// <summary>
/// One series split three ways: what persists, what repeats, and what is left.
/// Reconstruction is exact by construction — Trend + Oscillation + Noise equals the input.
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

/// <summary>
/// Singular Spectrum Analysis, the pre-processing step of the real-time path
/// (docs/llm_design.md): decomposes a short physiological series (typically 60 one-minute heart
/// rate samples) into <em>trend</em>, <em>oscillation</em> and <em>noise</em>, so the model
/// downstream reasons over a denoised level instead of raw sensor jitter.
/// <para>
/// Implemented via the lag-covariance route (Broomhead–King): embed the series into an
/// L-lagged trajectory matrix, eigen-decompose its L×L covariance with cyclic Jacobi rotations
/// (symmetric, tiny — exact enough and dependency-free, which the Application layer's
/// zero-package invariant requires), reconstruct one elementary series per leading eigenvector
/// by diagonal averaging, and group: the first component is trend, the next
/// <see cref="OscillationComponents"/> are oscillation, and noise is the exact residual.
/// </para>
/// <para>
/// Parameter defaults follow the design table in llm_design.md: a 30-sample window captures
/// roughly two cardiac-adjacent cycles and one activity micro-burst at 1-minute cadence, and
/// three components separate circadian drift plus short oscillation from noise.
/// </para>
/// </summary>
public static class SsaDecomposition
{
    /// <summary>Default embedding window (L) — 30 samples, per the design table.</summary>
    public const int DefaultWindowSize = 30;

    /// <summary>Eigen-components grouped into the oscillation series (after the trend's one).</summary>
    private const int OscillationComponents = 2;

    /// <summary>Jacobi sweep convergence threshold on the off-diagonal Frobenius mass.</summary>
    private const double JacobiEpsilon = 1e-12;

    private const int MaxJacobiSweeps = 50;

    /// <summary>
    /// Decomposes <paramref name="series"/>. Requires at least twice the window in samples —
    /// below that the embedding is too thin to separate anything, and the caller should not be
    /// assessing at all (mirrors the coverage-gate philosophy the baselines use).
    /// </summary>
    public static SsaResult Decompose(IReadOnlyList<double> series, int windowSize = DefaultWindowSize)
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

        var (eigenvalues, eigenvectors) = JacobiEigen(covariance);

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

        // The residual, exactly — never a reconstruction of the remaining components, so
        // Trend + Oscillation + Noise == input holds to floating-point identity.
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
    /// Cyclic Jacobi eigen-decomposition for a symmetric matrix — rotations zero one
    /// off-diagonal pair at a time until the off-diagonal mass is negligible. O(L³) per sweep,
    /// which at L=30 is microseconds; the classical stable choice when a dependency is not an
    /// option.
    /// </summary>
    private static (double[] Values, double[,] Vectors) JacobiEigen(double[,] matrix)
    {
        var l = matrix.GetLength(0);
        var a = (double[,])matrix.Clone();
        var vectors = new double[l, l];
        for (var i = 0; i < l; i++)
            vectors[i, i] = 1.0;

        for (var sweep = 0; sweep < MaxJacobiSweeps; sweep++)
        {
            var off = 0.0;
            for (var p = 0; p < l - 1; p++)
                for (var q = p + 1; q < l; q++)
                    off += a[p, q] * a[p, q];
            if (off < JacobiEpsilon)
                break;

            for (var p = 0; p < l - 1; p++)
            {
                for (var q = p + 1; q < l; q++)
                {
                    if (Math.Abs(a[p, q]) < double.Epsilon)
                        continue;

                    var theta = (a[q, q] - a[p, p]) / (2.0 * a[p, q]);
                    var t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1.0));
                    if (theta == 0.0)
                        t = 1.0;
                    var c = 1.0 / Math.Sqrt(t * t + 1.0);
                    var s = t * c;

                    for (var i = 0; i < l; i++)
                    {
                        var aip = a[i, p];
                        var aiq = a[i, q];
                        a[i, p] = c * aip - s * aiq;
                        a[i, q] = s * aip + c * aiq;
                    }

                    for (var i = 0; i < l; i++)
                    {
                        var api = a[p, i];
                        var aqi = a[q, i];
                        a[p, i] = c * api - s * aqi;
                        a[q, i] = s * api + c * aqi;
                    }

                    for (var i = 0; i < l; i++)
                    {
                        var vip = vectors[i, p];
                        var viq = vectors[i, q];
                        vectors[i, p] = c * vip - s * viq;
                        vectors[i, q] = s * vip + c * viq;
                    }
                }
            }
        }

        var values = new double[l];
        for (var i = 0; i < l; i++)
            values[i] = a[i, i];
        return (values, vectors);
    }
}
