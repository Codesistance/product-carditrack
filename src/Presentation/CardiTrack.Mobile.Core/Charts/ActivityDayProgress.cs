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
/// total and the bar sits full. That is one fill, not two colours — an overflow segment would
/// look like a second target, which is the metaphor this exists to avoid. Matching yesterday
/// is also full: the max only grows once today is strictly ahead.
/// </para>
/// <para>
/// Day n is the latest series point that actually has a value, so a morning with no steps yet
/// compares the last finished day to the day before it rather than drawing an empty bar under
/// yesterday's count. A missing day n−1 hides the bar rather than skipping back to an older
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
    /// today leads, the max is today and this is 1.
    /// </summary>
    public double Fill => (double)(Current / Math.Max(Current, Previous));

    /// <summary>
    /// True when the bar is today's running total against calendar yesterday. Any other pairing
    /// is a finished day against the day before it, and must not be labelled yesterday.
    /// </summary>
    public bool PreviousIsYesterday =>
        CurrentDate == SeriesEnd && PreviousDate == SeriesEnd.AddDays(-1);

    /// <summary>
    /// The caption under the bar when the card is not already showing "% of normal". Names the
    /// previous day's total, which is what the empty part of the track is.
    /// </summary>
    public string Caption =>
        PreviousIsYesterday ? $"Yesterday {Previous:N0}" : $"Previous day {Previous:N0}";

    /// <summary>
    /// What a screen reader hears in place of a fill width. The track has no text of its own.
    /// </summary>
    public string Description
    {
        get
        {
            var against = PreviousIsYesterday ? "yesterday" : "the previous day";
            if (Current > Previous)
                return $"{Current:N0} steps, more than {against}'s {Previous:N0}";
            if (Current == Previous)
                return $"{Current:N0} steps, matching {against}";
            return $"{Current:N0} of {against}'s {Previous:N0} steps";
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
