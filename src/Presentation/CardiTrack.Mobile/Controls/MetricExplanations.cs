using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// What each Key Metric Trends card is actually comparing a reading against, in the caregiver's
/// words: a line under the chart, and the fuller account behind the card's "i".
/// </summary>
/// <remarks>
/// <para>
/// The chart draws a dashed rule and a shaded band without ever saying what they mean. The legend
/// names them, but naming is not explaining — "Baseline 70" does not tell a non-clinical reader
/// that their own normal matters more than the published one, or that a wrist wearable is not
/// taking anyone's temperature.
/// </para>
/// <para>
/// Ranges are read off the metric's own <see cref="MetricReference"/> rather than written out
/// here, so a card can never contradict the band drawn above it — sleep's recommendation drops by
/// an hour from age 65, and the text follows the member rather than a constant.
/// </para>
/// </remarks>
internal static class MetricExplanations
{
    /// <summary>
    /// Closes every explanation. CardiTrack is not a diagnostic tool, and a panel that has just
    /// spent a paragraph on clinical reference ranges is exactly where that has to be said rather
    /// than assumed.
    /// </summary>
    private const string NotADiagnosis =
        "CardiTrack watches for changes from someone's own normal. It doesn't diagnose.";

    /// <summary>The line under the chart, and the panel behind the "i".</summary>
    internal static (string Footer, string Panel) For(string name, DashboardMetric metric, string format)
    {
        var (footer, panel) = Copy(name, metric, format);
        return (footer.Trim(), $"{panel}\n\n{NotADiagnosis}");
    }

    private static (string Footer, string Panel) Copy(string name, DashboardMetric metric, string format) =>
        name switch
        {
            "Activity" => (
                "Dashes: their usual day.",
                "The dashed line is this CardiMember's own usual step count, learned from their own "
                + "history — it is not a target. No health body publishes a step count to compare "
                + "anyone against, so this chart has no shaded band. A quieter day than usual is "
                + "worth noticing, not worrying about."),

            "Heart Rate" => (
                $"Dashes: their own normal.{Band(metric, format)}",
                "The dashed line is this CardiMember's own resting heart rate, learned over time. "
                + $"The shaded band is the typical adult range{Published(metric, format, "bpm")}. "
                + "Their own normal is the more useful of the two: a resting rate that is steady "
                + "for them can sit outside the published band and be perfectly ordinary for them."),

            "Sleep" => (
                $"Dashes: their usual night.{Band(metric, format)}",
                "The dashed line is this CardiMember's own usual night. The shaded band is the "
                + $"nightly sleep recommended for their age group{Published(metric, format, "hours")} "
                + "— the recommendation drops by an hour from age 65, so it is drawn for their age "
                + "rather than as a single adult figure. This measures how long they slept, not how "
                + "well."),

            "Skin Temp" => (
                "Dashes: their own nightly normal.",
                "A wrist wearable measures skin temperature, not core body temperature — this is "
                + "not a fever reading. The dashed line is the device's own nightly baseline for "
                + "this CardiMember, and what carries meaning is the distance from it rather than "
                + "the number itself. There is no published range to shade behind it, because there "
                + "is no population normal for a measurement this personal."),

            "Blood Oxygen" => (
                $"{Band(metric, format, lead: true)} No personal normal yet.",
                "The shaded band is the normal blood oxygen range"
                + $"{Published(metric, format, "%")}. CardiTrack has not learned a personal normal "
                + "for blood oxygen, so this chart has no dashed line — the readings are shown "
                + "against the published range alone."),

            "Breathing Rate" => (
                $"{Band(metric, format, lead: true)} No personal normal yet.",
                "The shaded band is the normal adult breathing rate"
                + $"{Published(metric, format, "breaths per minute")}. CardiTrack has not learned a "
                + "personal normal for breathing rate, so this chart has no dashed line."),

            // A metric added to the carousel without copy gets the honest minimum rather than a
            // card that silently loses its footer.
            _ => (
                $"{Band(metric, format, lead: true)}".Trim(),
                "The dashed line is this CardiMember's own normal for this reading, where one has "
                + "been learned; a shaded band is the published typical-adult range."),
        };

    /// <summary>
    /// The band as the footer says it. Leading space included so a metric with no published range
    /// contributes nothing rather than a dangling separator.
    /// </summary>
    private static string Band(DashboardMetric metric, string format, bool lead = false) =>
        metric.Reference is { } reference
            ? $"{(lead ? string.Empty : " ")}Band "
              + $"{string.Format(format, reference.Low)}–{string.Format(format, reference.High)} "
              + $"({reference.Source})."
            : string.Empty;

    /// <summary>The same range as the panel says it, spelled out with its unit and publisher.</summary>
    private static string Published(DashboardMetric metric, string format, string unit) =>
        metric.Reference is { } reference
            ? $" published by {reference.Source} ({string.Format(format, reference.Low)}–"
              + $"{string.Format(format, reference.High)} {unit})"
            : string.Empty;
}
