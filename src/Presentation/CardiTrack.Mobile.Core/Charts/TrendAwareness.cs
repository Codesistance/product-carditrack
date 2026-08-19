using System.Globalization;
using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Mobile.Core.Charts;

/// <summary>
/// The deterministic counting behind the daybook detail's trend lines — "under their usual on
/// 9 of the last 14 nights" — and the key line under each of its charts.
/// </summary>
/// <remarks>
/// <para>
/// Counts, never scores. The release matrix's standing decision is that trend interpretation
/// carries <b>no risk scores</b> (prediction cards went with the LSTM), and a count against the
/// member's own baseline is the strongest statement this screen is allowed to make: it is a fact a
/// caregiver can check against the chart drawn beside it, where a score would be a judgement they
/// could not. The screen labels these lines as awareness, not medical advice, and nothing here
/// computes anything the chart does not already show.
/// </para>
/// <para>
/// Lives in Mobile.Core for the reason <see cref="TrendScale"/> gives: the MAUI project cannot be
/// unit tested, and the arguable part of this is not the layout but which days get counted and
/// what the sentence claims about them.
/// </para>
/// </remarks>
public static class TrendAwareness
{
    /// <summary>How far back the counting looks — the same fortnight the charts draw.</summary>
    public const int WindowDays = 14;

    /// <summary>
    /// The floor under a claim. Below this many measured days the sentence would be counting
    /// noise: "under their usual on 2 of the last 3 nights" reads as a pattern and is a long
    /// weekend.
    /// </summary>
    public const int MinimumMeasuredDays = 7;

    /// <summary>Which side of the member's usual is the side worth a caregiver's awareness.</summary>
    public enum Direction
    {
        /// <summary>Below the usual is the reading worth noting — steps, sleep.</summary>
        BelowUsual,

        /// <summary>Above the usual is the reading worth noting — resting heart rate.</summary>
        AboveUsual,
    }

    /// <summary>
    /// The series with the current day — and anything later — removed: the window a daybook
    /// chart draws. A daybook reviews finished days, and today's point is a running total; the
    /// dashboard series marks it <see cref="MetricPoint.IsPartial"/> only on alert step charts,
    /// so by the time it reaches this screen it looks like an ordinary point and a quiet morning
    /// draws as a collapse. Cut by date rather than by that flag for exactly this reason — and
    /// strictly-before also drops a point a member ahead of the reader has already filed under
    /// the reader's tomorrow.
    /// </summary>
    /// <param name="series">The metric's series, oldest first, as the dashboard reports it.</param>
    /// <param name="today">The reader's current day, passed in so the cut is testable.</param>
    public static List<MetricPoint> ExcludingToday(IEnumerable<MetricPoint>? series, DateOnly today)
        => series?.Where(p => p.Date < today).ToList() ?? new();

    /// <summary>
    /// The awareness line for one metric, or null when there is nothing honest to say — no
    /// baseline to count against, or too few measured days for the count to mean anything.
    /// </summary>
    /// <param name="series">The metric's series, oldest first, as the dashboard reports it.</param>
    /// <param name="baseline">The member's own usual for this metric.</param>
    /// <param name="direction">Which side of that usual gets counted.</param>
    /// <param name="dayWord">
    /// What one counted day is called in the sentence — "day" for steps, "night" for sleep — so
    /// the line reads the way a person would say it.
    /// </param>
    public static string? Line(
        IReadOnlyList<MetricPoint>? series,
        decimal? baseline,
        Direction direction,
        string dayWord)
    {
        if (baseline is not { } usual)
            return null;

        return CountedLine(series, usual, direction, "their usual", dayWord);
    }

    /// <summary>
    /// The same counted sentence against a <b>published</b> bound rather than the member's own
    /// usual — "Under the recommended 7h (NSF) on 9 of the last 13 nights". Null under the same
    /// conditions <see cref="Line"/> is.
    /// </summary>
    /// <remarks>
    /// This is the entry page's only inferential claim beyond the member's own baseline, and it is
    /// allowed exactly because the bound is not ours: every value that reaches
    /// <paramref name="boundText"/> comes from <c>HealthReferenceRanges</c>, where each range
    /// carries the body that publishes it (NSF, AHA, WHO) — and the sentence names that source, so
    /// the reader can see whose recommendation is being counted against. A bound with nobody's
    /// name on it does not belong here.
    /// </remarks>
    /// <param name="bound">The published figure to count against.</param>
    /// <param name="direction">Which side of the bound is the side worth counting.</param>
    /// <param name="boundText">
    /// The bound as the sentence says it, with its unit and its publisher — "the recommended 7h
    /// (NSF)". Built by the caller, which knows the metric's format.
    /// </param>
    /// <param name="dayWord">"day" or "night", as in <see cref="Line"/>.</param>
    public static string? BandLine(
        IReadOnlyList<MetricPoint>? series,
        decimal bound,
        Direction direction,
        string boundText,
        string dayWord)
        => CountedLine(series, bound, direction, boundText, dayWord);

    /// <summary>
    /// The shared counting: finished, measured days only, from the same fortnight the chart
    /// draws. A partial day is a running total and would count as "under" every morning; an
    /// unmeasured day is silence, and counting silence as either side is the confusion this
    /// product exists to prevent. Below <see cref="MinimumMeasuredDays"/> the sentence would be
    /// counting noise, so nothing is said at all.
    /// </summary>
    private static string? CountedLine(
        IReadOnlyList<MetricPoint>? series,
        decimal threshold,
        Direction direction,
        string thresholdText,
        string dayWord)
    {
        if (series is null)
            return null;

        var counted = series
            .TakeLast(WindowDays)
            .Where(p => p.Value is not null && !p.IsPartial)
            .ToList();

        if (counted.Count < MinimumMeasuredDays)
            return null;

        var offSide = direction == Direction.BelowUsual
            ? counted.Count(p => p.Value < threshold)
            : counted.Count(p => p.Value > threshold);

        var side = direction == Direction.BelowUsual ? "under" : "above";
        var days = string.Create(
            CultureInfo.CurrentCulture, $"{offSide} of the last {counted.Count} {dayWord}s");

        return $"{Capitalize(side)} {thresholdText} on {days}";
    }

    private static string Capitalize(string word) =>
        char.ToUpperInvariant(word[0]) + word[1..];
}

/// <summary>
/// The one line under a daybook trend chart that says what its marks are — the same sentence
/// <see cref="AlertChartKey"/> writes for the alert detail, built from a <see cref="DashboardMetric"/>
/// instead of an alert chart payload. The published band names its source (NSF, AHA, WHO), because
/// a shaded band with nobody's name on it is a claim wearing no authority.
/// </summary>
public static class MetricChartKey
{
    /// <summary>
    /// The key for this metric's chart, or null when it has neither mark to name.
    /// </summary>
    /// <param name="metric">The metric the chart is rendered from — same object, nothing else.</param>
    /// <param name="axisFormat">The bare axis format the chart's own labels use.</param>
    public static string? For(DashboardMetric metric, string axisFormat)
    {
        var entries = new List<string>(2);

        if (metric.Baseline is { } usual)
            entries.Add($"Dashed: their usual {string.Format(CultureInfo.CurrentCulture, axisFormat, usual)}");

        if (metric.Reference is { } reference)
        {
            entries.Add(
                $"Shaded: recommended {string.Format(CultureInfo.CurrentCulture, axisFormat, reference.Low)}"
                + $"–{string.Format(CultureInfo.CurrentCulture, axisFormat, reference.High)}"
                + $" ({reference.Source})");
        }

        return entries.Count == 0 ? null : string.Join(AlertChartKey.Separator, entries);
    }
}
