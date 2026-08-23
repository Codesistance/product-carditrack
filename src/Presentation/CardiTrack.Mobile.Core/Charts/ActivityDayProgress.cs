using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Mobile.Core.Charts;

/// <summary>
/// How far the Activity Key Metrics bar is through the previous calendar day's total — and how
/// the track's max moves once today overtakes it.
/// </summary>
/// <remarks>
/// <para>
/// The bar is not a goal. <see cref="DashboardMetric.Goal"/> is this member's usual day, which
/// nobody set as a target, and filling a track against it made a fitness-app "hit 100%" reading
/// out of a caregiver comparison. Day n against day n−1 is a comparison a caregiver can make
/// without a baseline and without inventing a round number.
/// </para>
/// <para>
/// The track's max is yesterday's total until today exceeds it; then the max becomes today's
/// total. When today is ahead, the extra is a second stacked fill — yesterday's share in one
/// colour, the overflow in another — so beating yesterday is not indistinguishable from
/// matching it. Matching yesterday is a single full fill: the max only grows once today is
/// strictly ahead.
/// </para>
/// <para>
/// Day n is the latest series point that actually has a value, so a morning with no
/// row yet (value <c>null</c>) compares the last finished day to the day before it
/// rather than drawing an empty bar under yesterday's count. An explicit <c>0</c> is
/// a reading — they have been counted today and have not moved — and fills 0 against
/// yesterday. A missing day n−1 hides the bar rather than skipping back to an older
/// day: a gap is not yesterday.
/// </para>
/// </remarks>
public readonly record struct ActivityDayProgress
{
    private ActivityDayProgress(
        decimal current, decimal previous, DateOnly currentDate, DateOnly previousDate, DateOnly seriesEnd)
    {
        Current = current;
        Previous = previous;
        CurrentDate = currentDate;
        PreviousDate = previousDate;
        SeriesEnd = seriesEnd;
    }

    /// <summary>The day the card's step count belongs to.</summary>
    public decimal Current { get; }

    /// <summary>The calendar day immediately before <see cref="CurrentDate"/>.</summary>
    public decimal Previous { get; }

    public DateOnly CurrentDate { get; }
    public DateOnly PreviousDate { get; }

    /// <summary>
    /// The last date in the series — always today on the dashboard payload — so the caption can
    /// say "yesterday" only when that is actually the day being compared.
    /// </summary>
    public DateOnly SeriesEnd { get; }

    /// <summary>
    /// <see cref="Current"/> as a fraction of <c>max(current, previous)</c>. Always in 0–1: once
    /// today leads, the max is today and this is 1. Equal to <see cref="Compared"/> +
    /// <see cref="Overflow"/>.
    /// </summary>
    public double Fill => Compared + Overflow;

    /// <summary>
    /// The share of the track that is the previous day's total (or today's running count, when
    /// that is still behind). The first stacked colour.
    /// </summary>
    public double Compared => (double)(Math.Min(Current, Previous) / Scale);

    /// <summary>
    /// The share of the track that is today past yesterday. Zero until today is strictly ahead;
    /// then it is the second stacked colour. Matching yesterday is not overflow.
    /// </summary>
    public double Overflow => (double)(Math.Max(0m, Current - Previous) / Scale);

    private decimal Scale => Math.Max(Current, Previous);

    /// <summary>
    /// True when the bar is today's running total against calendar yesterday. Any other pairing
    /// is a finished day against the day before it, and must not be labelled yesterday.
    /// </summary>
    public bool PreviousIsYesterday =>
        CurrentDate == SeriesEnd && PreviousDate == SeriesEnd.AddDays(-1);

    /// <summary>
    /// The caption under the bar. A comparison with the previous day — "vs 5,000 yesterday" — not
    /// a remainder against a target, and not the comparison with the member's own normal that the
    /// percentage beside the reading already makes.
    /// </summary>
    public string Caption =>
        PreviousIsYesterday ? $"vs {Previous:N0} yesterday" : $"vs previous day's {Previous:N0}";

    /// <summary>
    /// What a screen reader hears in place of a fill width. The same comparison as
    /// <see cref="Caption"/>, with both days named, and no "of" that would read as a goal.
    /// </summary>
    public string Description
    {
        get
        {
            var against = PreviousIsYesterday ? "yesterday" : "the previous day";
            return $"{Current:N0} steps vs {Previous:N0} {against}";
        }
    }

    /// <summary>
    /// The bar to draw for this metric, or null when there is no honest previous day to fill
    /// against — no reading yet, a gap on day n−1, or both days at zero (a zero max).
    /// </summary>
    public static ActivityDayProgress? For(DashboardMetric metric) => For(metric.Series);

    public static ActivityDayProgress? For(IReadOnlyList<MetricPoint> series)
    {
        if (series is not { Count: > 0 })
            return null;

        MetricPoint? current = null;
        for (var i = series.Count - 1; i >= 0; i--)
        {
            if (series[i].Value is not null)
            {
                current = series[i];
                break;
            }
        }

        if (current is not { Value: { } currentValue })
            return null;

        var previousDate = current.Date.AddDays(-1);
        MetricPoint? previous = null;
        for (var i = 0; i < series.Count; i++)
        {
            if (series[i].Date == previousDate)
            {
                previous = series[i];
                break;
            }
        }

        if (previous is not { Value: { } previousValue })
            return null;

        var max = Math.Max(currentValue, previousValue);
        if (max <= 0)
            return null;

        return new ActivityDayProgress(
            currentValue, previousValue, current.Date, previousDate, series[^1].Date);
    }
}
