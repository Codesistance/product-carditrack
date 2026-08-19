namespace CardiTrack.Mobile.Core.Charts;

/// <summary>
/// The vertical extent a metric trend chart plots over, once the member's baseline and the
/// published reference range have been fitted around the readings themselves.
/// </summary>
/// <remarks>
/// Lives here rather than beside the drawable that uses it because it is the one genuinely
/// arguable part of drawing those two lines — the rule for when a reference range is allowed to
/// rescale the chart — and the MAUI project cannot be unit tested.
/// </remarks>
public readonly record struct TrendScale
{
    /// <summary>
    /// How far the reference range and the baseline may stretch the readings' own extent before
    /// they are refused it, as a multiple of that extent.
    /// </summary>
    /// <remarks>
    /// Set from the widest band against the tightest metric it has to serve: a resting heart rate
    /// moving over 10 bpm across a month, read against a 60–100 reference, needs four times its own
    /// extent before the band fits — and that chart is worth drawing, because where a member sits
    /// inside the normal range is the comparison the band exists to make. Six leaves headroom
    /// around that case. Past it the readings stop being a trend at all: a heart rate that held
    /// inside two beats all month would flatten to a straight line under the same band, which
    /// trades the thing the caregiver came to see for context the legend already carries in words.
    /// </remarks>
    public const double Headroom = 6d;

    /// <summary>
    /// How far the plot's floor sits below the lowest reading, as a fraction of the plotted
    /// extent, when the readings themselves set that floor.
    /// </summary>
    /// <remarks>
    /// Without it the smallest sample is drawn on the axis minimum itself — and now that the
    /// charts write their extent on the axis, a reading sitting exactly on the bottom number
    /// reads as a value pinned to a floor the chart imposed rather than one the member reached.
    /// Only the readings' own floor is padded: a baseline or reference bound that reached lower
    /// has already put clear air under the samples. Never taken below zero, which for every
    /// metric drawn here — hours, beats, percent, steps — is a real bound rather than a
    /// convention; a floor already at zero stays there.
    /// </remarks>
    public const double FloorPadding = 0.1;

    private TrendScale(double min, double max)
    {
        Min = min;
        Max = max;
    }

    public double Min { get; }
    public double Max { get; }

    /// <summary>
    /// False for a window whose readings are all the same value and carry nothing to widen it —
    /// there is no extent to normalise against, and the caller draws the line down the middle.
    /// </summary>
    public bool HasExtent => Max > Min;

    /// <summary>Whether a value can be drawn where it belongs rather than clamped to an edge.</summary>
    public bool Contains(double value) => value >= Min && value <= Max;

    /// <summary>
    /// Fits the plot around the readings, then around the baseline and the reference range in that
    /// order of priority — each is admitted only while <see cref="Headroom"/> allows.
    /// </summary>
    /// <remarks>
    /// A rejected reference range still gets drawn, clipped to the plot: it is an area, so running
    /// off the top or bottom edge reads correctly as continuing beyond the chart. A rejected
    /// baseline does not — a line pinned to the edge would read as a baseline sitting exactly
    /// there — so the caller drops it and leaves the legend to carry the number. See
    /// <see cref="Contains"/>.
    /// </remarks>
    public static TrendScale For(
        double dataMin, double dataMax, double? baseline, double? referenceLow, double? referenceHigh)
    {
        var min = Math.Min(dataMin, dataMax);
        var max = Math.Max(dataMin, dataMax);
        var extent = max - min;

        if (baseline is { } value)
            (min, max) = Admit(min, max, Math.Min(value, min), Math.Max(value, max), extent);

        if (referenceLow is not null || referenceHigh is not null)
        {
            var low = referenceLow is { } l ? Math.Min(l, min) : min;
            var high = referenceHigh is { } h ? Math.Max(h, max) : max;
            (min, max) = Admit(min, max, low, high, extent);
        }

        // See FloorPadding. Applied last so the padding can never buy a rejected baseline or
        // band its way in, and only when the lowest reading itself is what the plot bottoms
        // out on.
        if (max > min && min == Math.Min(dataMin, dataMax) && min > 0)
            min = Math.Max(0, min - (max - min) * FloorPadding);

        return new TrendScale(min, max);
    }

    /// <param name="extent">
    /// The readings' own extent — every candidate is measured against that rather than against the
    /// running total, so a baseline that has already widened the plot cannot then let a reference
    /// range in that would have been refused on its own.
    /// </param>
    private static (double Min, double Max) Admit(
        double min, double max, double candidateMin, double candidateMax, double extent)
    {
        // A flat window has no extent to be swamped, and everything it can be widened by is an
        // improvement on drawing it down the middle of an unlabelled plot.
        if (extent <= 0)
            return (candidateMin, candidateMax);

        return candidateMax - candidateMin <= extent * Headroom ? (candidateMin, candidateMax) : (min, max);
    }
}
