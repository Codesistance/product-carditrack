namespace CardiTrack.Mobile.Core.Charts;

/// <summary>
/// The three star-column widths behind a stacked progress track — the compared share, the extra
/// stacked on top of it, and the empty remainder — guaranteed safe to hand to a layout.
/// </summary>
/// <remarks>
/// <para>
/// This exists as a value rather than as arithmetic inline in the control because the arithmetic
/// is where the track broke: a column width is one <c>GridLength</c> away from throwing, and a
/// throw while applying a metric takes the whole dashboard down, not just the bar. As a pure
/// function it can be tested against real step counts without a UI host.
/// </para>
/// <para>
/// The invariant callers rely on: every width is a non-negative number, so none of them can be
/// rejected by <c>GridLength</c>. Star columns are normalised against their own total by the
/// layout, so clamping the remainder costs no accuracy in the drawn proportions.
/// </para>
/// </remarks>
public readonly record struct StackedTrack
{
    private StackedTrack(double compared, double overflow, double remainder)
    {
        Compared = compared;
        Overflow = overflow;
        Remainder = remainder;
    }

    /// <summary>The first fill — the day being compared against (or today, while still behind).</summary>
    public double Compared { get; }

    /// <summary>The second fill, stacked after <see cref="Compared"/>. Zero when today is not ahead.</summary>
    public double Overflow { get; }

    /// <summary>The unfilled tail of the track. Never negative.</summary>
    public double Remainder { get; }

    /// <summary>True when there is a second fill to draw.</summary>
    public bool IsStacked => Overflow > 0;

    /// <summary>
    /// Lays out the two fills and the remainder, keeping every width non-negative.
    /// </summary>
    /// <remarks>
    /// The remainder is clamped rather than trusted to fall out of the subtraction.
    /// <see cref="ActivityDayProgress.Compared"/> and <see cref="ActivityDayProgress.Overflow"/>
    /// arrive as two independently rounded decimal-to-double conversions of shares that sum to
    /// exactly 1. When the true sum lands just above 1, the <c>double</c> sum rounds back down to
    /// exactly 1.0 — so a <c>&gt; 1</c> test does not fire — while <c>1 - compared - overflow</c>
    /// lands a few ulps *below* zero. Roughly a fifth of "today ahead of yesterday" step pairs do
    /// this; 5 steps against yesterday's 4 is enough.
    /// </remarks>
    public static StackedTrack For(double compared, double overflow)
    {
        compared = Sanitise(compared);
        overflow = Sanitise(overflow);

        // Yesterday's share always keeps its width; the extra gives way, so a bar that is somehow
        // over-full is drawn full rather than drawn wrong.
        if (compared + overflow > 1)
            overflow = 1 - compared;

        return new StackedTrack(compared, overflow, Math.Max(0, 1 - compared - overflow));
    }

    /// <summary>
    /// A share as a width: inside 0–1, and a number. NaN is treated as no fill rather than passed
    /// on — a track that silently draws empty beats one that throws on a health screen.
    /// </summary>
    private static double Sanitise(double value) =>
        double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
}
