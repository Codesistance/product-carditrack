using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Mobile.Core.Charts;

/// <summary>
/// The one line under the alert detail chart that says what its two marks are: the dashed rule at
/// the member's own usual, and — for a metric a standards body publishes a range for — the shaded
/// band behind the line.
/// </summary>
/// <remarks>
/// <para>
/// Lives here rather than in the page for the reason <see cref="TrendScale"/> gives: the MAUI
/// project cannot be unit tested, and the arguable part of this is not the drawing but which marks
/// the sentence claims are on the chart. A key that names a band the chart did not draw is worse
/// than no key at all, so it is built from the same response the chart is rendered from and
/// nothing else.
/// </para>
/// <para>
/// One line rather than the dashboard's two-swatch legend grid (<c>MetricTrendCard</c>): this card
/// carries a single chart under a message that has already named both figures in words, and a
/// legend grid here would be more chrome than key.
/// </para>
/// </remarks>
public static class AlertChartKey
{
    /// <summary>Between the two entries when the chart carries both.</summary>
    public const string Separator = "  ·  ";

    /// <summary>
    /// The key for this chart, or null when it has neither mark — a member still being learned has
    /// no usual to rule, and no standards body publishes a daily step count.
    /// </summary>
    public static string? For(AlertChartResponse chart)
    {
        var axis = AxisFormat(chart.Metric);
        var entries = new List<string>(2);

        // The dash is drawn in the line's own ink and reads first, so it is named first.
        if (chart.Baseline is { } usual)
            entries.Add($"Dashed: their usual {string.Format(axis, usual)}");

        if (chart.Reference is { } reference)
        {
            entries.Add(
                $"Shaded: recommended {string.Format(axis, reference.Low)}"
                + $"–{string.Format(axis, reference.High)} ({reference.Source})");
        }

        return entries.Count == 0 ? null : string.Join(Separator, entries);
    }

    /// <summary>
    /// How a figure is written when it names a level rather than a reading — no unit, because the
    /// entry it sits in has already said which chart this is.
    /// </summary>
    /// <remarks>
    /// The fractional metrics are the ones a whole number misstates: the hour-denominated charts
    /// (a usual night of 3.8, a usual longest stretch of 2.4) and the per-minute overnight rates.
    /// Rounding the 2.4-hour usual to "2" put the dashed rule's own key visibly at odds with the
    /// comparison card's "2.4 h" on the same screen.
    /// </remarks>
    public static string AxisFormat(string metric) => metric switch
    {
        "sleep" or "longestSedentaryStretch" or "overnightBreathingRate" or "heartRateVariability"
            => "{0:0.#}",
        _ => "{0:N0}",
    };

    /// <summary>
    /// The chart card's headline figure, unit and all — "6.2 hours", "1,477 steps", "68 bpm".
    /// </summary>
    /// <remarks>
    /// The unit is the response's own <see cref="AlertChartResponse.Unit"/>, never guessed from
    /// the metric name: the page used to hold its own metric→unit switch whose fallback was
    /// "bpm", and the first chart the server added that the app had not heard of — the
    /// hours-denominated still stretch — was captioned "1 bpm" under a heart icon, which read as
    /// heart-rate-derived inactivity detection. An unknown metric now formats as a whole number
    /// in whatever unit the server named, and no unit at all when it named none.
    /// </remarks>
    public static string Value(AlertChartResponse chart, decimal value)
    {
        var figure = string.Format(AxisFormat(chart.Metric), value);
        return string.IsNullOrWhiteSpace(chart.Unit) ? figure : $"{figure} {chart.Unit}";
    }
}
