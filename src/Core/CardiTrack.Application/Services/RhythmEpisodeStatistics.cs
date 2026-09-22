namespace CardiTrack.Application.Services;

/// <summary>
/// The summary figures stored alongside an episode's raw intervals. Pure, so the one piece of
/// arithmetic here that is easy to get quietly wrong — RMSSD, which is over <em>successive
/// differences</em> and not over the intervals themselves — is testable without a database or a
/// provider.
/// </summary>
public static class RhythmEpisodeStatistics
{
    /// <summary>
    /// Mean, minimum and maximum interbeat interval in milliseconds, and RMSSD where the window
    /// holds enough beats to have successive differences at all.
    /// </summary>
    /// <remarks>
    /// An empty window returns zeros and a null RMSSD rather than throwing: a notification whose
    /// beats the provider did not serve is still a notification, and the row exists so a
    /// caregiver's episode list cannot disagree with their notification count. Readers tell that
    /// case apart by <c>BeatCount</c>, not by the statistics.
    /// </remarks>
    public static (int Mean, int Min, int Max, int? Rmssd) Summarise(IReadOnlyList<int> rrMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(rrMilliseconds);

        if (rrMilliseconds.Count == 0)
            return (0, 0, 0, null);

        long total = 0;
        var min = int.MaxValue;
        var max = int.MinValue;

        foreach (var rr in rrMilliseconds)
        {
            total += rr;
            if (rr < min) min = rr;
            if (rr > max) max = rr;
        }

        var mean = (int)(total / rrMilliseconds.Count);

        return (mean, min, max, Rmssd(rrMilliseconds));
    }

    /// <summary>
    /// Root mean square of successive differences, milliseconds, rounded to the nearest whole.
    /// Null below two beats, where no successive difference exists — distinct from zero, which is
    /// a real and remarkable answer meaning every interval was identical.
    /// </summary>
    private static int? Rmssd(IReadOnlyList<int> rr)
    {
        if (rr.Count < 2)
            return null;

        // Accumulated as double rather than long: squared millisecond differences over a long
        // window stay well inside double's exact-integer range, and the division that follows
        // would need the cast anyway.
        var sumOfSquares = 0d;
        for (var i = 1; i < rr.Count; i++)
        {
            var delta = (double)rr[i] - rr[i - 1];
            sumOfSquares += delta * delta;
        }

        return (int)Math.Round(Math.Sqrt(sumOfSquares / (rr.Count - 1)), MidpointRounding.AwayFromZero);
    }
}
