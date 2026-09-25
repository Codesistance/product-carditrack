using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Mobile.Core.Charts;

/// <summary>
/// Which published range a chart may draw, and what its key calls it. One place, so the Member
/// Detail trends, the chat charts and the alert chart cannot name one range two ways.
/// </summary>
/// <remarks>
/// <para>
/// For sleep, resting heart rate and blood oxygen the range is what normal means (decision
/// 2026-09-25, <see cref="MetricReference.IsPublishedNormal"/>): it is the chart's primary
/// reference and the member's usual is the secondary one. Every other range stays background and
/// keeps the "Typical" wording it always had.
/// </para>
/// <para>
/// Breathing while asleep never gets a band. WHO's 12–20 is a rate at rest, and a rate measured
/// across hours of sleep is not that measurement (<c>HealthReferenceRanges.NoOvernightBreathingBand</c>).
/// The server sends none on the routes that draw it; <see cref="Drawable"/> makes sure a band that
/// arrived by some other route is still not drawn.
/// </para>
/// </remarks>
public static class ReferenceBands
{
    /// <summary>
    /// The metric keys and series names that mean breathing while asleep: the alert chart's
    /// <c>AlertChartResponse.Metric</c> and the chat reply's series name.
    /// </summary>
    private static readonly HashSet<string> AsleepBreathing = new(StringComparer.OrdinalIgnoreCase)
    {
        "overnightBreathingRate",
        "Breathing while asleep",
    };

    /// <summary>The range to draw behind <paramref name="metric"/>, or null when none may be.</summary>
    public static MetricReference? Drawable(string metric, MetricReference? reference) =>
        reference is null || AsleepBreathing.Contains(metric) ? null : reference;

    /// <summary>
    /// What the key calls the range: "Normal" where it is the normal, "Typical" where it is only
    /// background.
    /// </summary>
    public static string Noun(MetricReference reference) =>
        reference.IsPublishedNormal ? "Normal" : "Typical";
}
