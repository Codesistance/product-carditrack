using CardiTrack.Application.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins the decomposition's contract from the consumer's side: exact reconstruction, sane
/// separation on signals whose ground truth is known, and hard guards on inputs too thin to
/// separate anything.
/// </summary>
public class SsaDecompositionTests
{
    private const int N = 60;

    private static double[] Series(Func<int, double> f) =>
        Enumerable.Range(0, N).Select(f).ToArray();

    // The property every consumer silently relies on: nothing is invented or lost beyond
    // floating-point rounding (hence the precision bound, not bit equality).
    [Fact]
    public void Reconstruction_RecoversTheInput_WithinRounding()
    {
        var series = Series(t => 70 + 0.1 * t + 4 * Math.Sin(2 * Math.PI * t / 8) + ((t * 37) % 11 - 5) * 0.3);

        var result = SsaDecomposition.Decompose(series);

        for (var t = 0; t < N; t++)
        {
            Assert.Equal(series[t], result.Trend[t] + result.Oscillation[t] + result.Noise[t], precision: 9);
        }
    }

    [Fact]
    public void AConstantSeries_IsAllTrend()
    {
        var result = SsaDecomposition.Decompose(Series(_ => 72.0));

        Assert.All(result.Trend, v => Assert.Equal(72.0, v, precision: 6));
        Assert.Equal(0.0, result.NoiseRms, precision: 6);
    }

    [Fact]
    public void ALinearRamp_LandsInTrend_NotNoise()
    {
        var series = Series(t => 60.0 + 0.5 * t);

        var result = SsaDecomposition.Decompose(series);

        // The trend must carry the ramp: its last value sits near the series' end, far from its
        // start, and what is left over is small relative to the 30-bpm climb.
        Assert.InRange(result.TrendLast, 80.0, 92.0);
        Assert.True(result.NoiseRms < 1.0,
            $"a clean ramp should leave almost nothing in noise, got RMS {result.NoiseRms:F3}");
    }

    [Fact]
    public void APeriodicSignal_LandsInOscillation_NotNoise()
    {
        // Constant level + strong 8-sample cycle, no injected noise.
        var series = Series(t => 70.0 + 5.0 * Math.Sin(2 * Math.PI * t / 8));

        var result = SsaDecomposition.Decompose(series);

        var oscillationPower = result.Oscillation.Sum(v => v * v);
        var noisePower = result.Noise.Sum(v => v * v);
        Assert.True(oscillationPower > noisePower,
            $"the cycle should be captured as oscillation (osc {oscillationPower:F1} vs noise {noisePower:F1})");
    }

    // The consumer's actual question: does a genuine level shift stand out against NoiseRms?
    [Fact]
    public void AJitterySeries_YieldsANoiseYardstick_ThatAShiftClears()
    {
        // ±1-ish deterministic jitter around 70, then measure a +10 departure against NoiseRms.
        var series = Series(t => 70.0 + ((t * 37) % 7 - 3) * 0.4);

        var result = SsaDecomposition.Decompose(series);

        Assert.True(result.NoiseRms is > 0.05 and < 2.0,
            $"jitter of ~1 bpm should be the noise scale, got {result.NoiseRms:F3}");
        var deviationScore = Math.Abs(80.0 - result.TrendLast) / result.NoiseRms;
        Assert.True(deviationScore > 3.0,
            $"a +10 bpm departure must clear a 3-sigma-style gate, scored {deviationScore:F1}");
    }

    [Fact]
    public void ASeriesShorterThanTwiceTheWindow_IsRefused()
    {
        Assert.Throws<ArgumentException>(() =>
            SsaDecomposition.Decompose(Enumerable.Repeat(70.0, 59).ToArray(), windowSize: 30));
    }

    [Fact]
    public void ADegenerateWindow_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SsaDecomposition.Decompose(Series(_ => 70.0), windowSize: 1));
    }
}
