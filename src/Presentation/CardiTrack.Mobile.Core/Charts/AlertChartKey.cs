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
    public static string AxisFormat(string metric) => metric switch
    {
        "sleep" => "{0:0.#}",
        _ => "{0:N0}",
    };
}
