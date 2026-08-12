using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Mobile.Core.Charts;

/// <summary>
/// What each Key Metric Trends card is actually comparing a reading against, in the caregiver's
/// words: a line under the chart, and the fuller account behind the card's "i". Also the name the
/// card's legend gives the dashed rule, so the key, the footer and the panel all call it the same
/// thing.
/// </summary>
/// <remarks>
/// <para>
/// The chart draws a dashed rule and a shaded band without ever saying what they mean. The legend
/// names them, but naming is not explaining — "Their usual day: 5,432" does not tell a
/// non-clinical reader that their own normal matters more than the published one, or that a wrist
/// wearable is not taking anyone's temperature.
/// </para>
/// <para>
/// Both marks are optional, and the copy follows the chart rather than describing an idealised
/// one: a member whose baseline is still learning has no dashed rule to point at, and saying
/// "Dashes: their usual day" over a chart with no dashes is worse than saying nothing. Ranges are
/// read off the metric's own <see cref="MetricReference"/> for the same reason — sleep's
/// recommendation drops by an hour from age 65, and the text follows the member rather than a
/// constant.
/// </para>
/// </remarks>
public static class MetricExplanations
{
    /// <summary>
    /// Closes every explanation. CardiTrack is not a diagnostic tool, and a panel that has just
    /// spent a paragraph on clinical reference ranges is exactly where that has to be said rather
    /// than assumed.
    /// </summary>
    private const string NotADiagnosis =
        "CardiTrack watches for changes from someone's own normal. It doesn't diagnose.";

    /// <summary>The line under the chart, and the panel behind the "i".</summary>
    public static (string Footer, string Panel) For(string name, DashboardMetric metric, string format)
    {
        var panel = Panel(name, metric, format);
        return (Footer(name, metric, format), $"{panel}\n\n{NotADiagnosis}");
    }

    /// <summary>
    /// What this metric's dashed rule is called on the card's legend — the same phrase the footer
    /// and the panel use for it, so the key names the mark rather than introducing a second term
    /// for it. Sentence case: it opens a legend entry.
    /// </summary>
    /// <remarks>
    /// Only ever reached for a metric that has a baseline to label, so the fallback covers a metric
    /// added to the carousel without copy rather than the metrics that never learn one.
    /// </remarks>
    public static string BaselineLabel(string name)
    {
        var noun = BaselineNoun(name) ?? "their own normal";
        return char.ToUpperInvariant(noun[0]) + noun[1..];
    }

    /// <summary>
    /// What this metric's own learned normal is called, in the caregiver's words, or null for a
    /// metric CardiTrack has no baseline concept for at all — SpO2 and breathing rate, which are
    /// shown against the published range alone rather than against a normal they are still
    /// waiting on.
    /// </summary>
    private static string? BaselineNoun(string name) =>
        name switch
        {
            "Activity" => "their usual day",
            "Heart Rate" => "their own normal",
            "Sleep" => "their usual night",
            "Skin Temp" => "their nightly normal",
            "Blood Oxygen" or "Breathing Rate" => null,
            _ => "their own normal",
        };

    /// <summary>
    /// The strip under the chart: what the dashed rule is, and what the band is, for whichever of
    /// the two this card actually draws.
    /// </summary>
    /// <remarks>
    /// Assembled from fragments rather than written out per metric, because which fragments apply
    /// is a property of the member's data, not of the metric — every metric that can learn a
    /// baseline spends its first weeks without one.
    /// </remarks>
    private static string Footer(string name, DashboardMetric metric, string format)
    {
        var parts = new List<string>(2);

        if (BaselineNoun(name) is not { } noun)
            parts.Add("No personal normal for this reading.");
        else if (metric.Baseline is not null)
            parts.Add($"Dashes: {noun}.");
        else
            parts.Add($"Still learning {noun}.");

        if (metric.Reference is { } reference)
            parts.Add($"Band {string.Format(format, reference.Low)}–{string.Format(format, reference.High)} "
                      + $"({reference.Source}).");

        return string.Join(" ", parts);
    }

    /// <summary>
    /// The fuller account behind the "i". Each metric answers the same two questions — what the
    /// dashed rule is, and what the band is — in whichever form matches what is on the chart.
    /// </summary>
    private static string Panel(string name, DashboardMetric metric, string format)
    {
        var learned = metric.Baseline is not null;

        return name switch
        {
            "Activity" => learned
                ? "The dashed line is this CardiMember's own usual step count, learned from their own "
                  + "history — it is not a target. No health body publishes a step count to compare "
                  + "anyone against, so this chart has no shaded band. A quieter day than usual is "
                  + "worth noticing, not worrying about."
                : "CardiTrack is still learning this CardiMember's usual step count from their own "
                  + "history, so there is no dashed line on this chart yet. No health body publishes "
                  + "a step count to compare anyone against either, so there is no shaded band. Until "
                  + "their usual day is known, the shape of the line is the thing to read.",

            "Heart Rate" => learned
                ? "The dashed line is this CardiMember's own resting heart rate, learned over time. "
                  + $"The shaded band is the typical adult range{Published(metric, format, "bpm")}. "
                  + "Their own normal is the more useful of the two: a resting rate that is steady "
                  + "for them can sit outside the published band and be perfectly ordinary for them."
                : "CardiTrack is still learning this CardiMember's own resting heart rate, so there "
                  + "is no dashed line on this chart yet. The shaded band is the typical adult range"
                  + $"{Published(metric, format, "bpm")}. Once their own normal is known it becomes "
                  + "the more useful of the two: a resting rate that is steady for them can sit "
                  + "outside the published band and be perfectly ordinary for them.",

            "Sleep" => learned
                ? "The dashed line is this CardiMember's own usual night. The shaded band is the "
                  + $"nightly sleep recommended for their age group{Published(metric, format, "hours")} "
                  + "— the recommendation drops by an hour from age 65, so it is drawn for their age "
                  + "rather than as a single adult figure. This measures how long they slept, not how "
                  + "well."
                : "CardiTrack is still learning this CardiMember's usual night, so there is no dashed "
                  + "line on this chart yet. The shaded band is the nightly sleep recommended for "
                  + $"their age group{Published(metric, format, "hours")} — the recommendation drops "
                  + "by an hour from age 65, so it is drawn for their age rather than as a single "
                  + "adult figure. This measures how long they slept, not how well.",

            "Skin Temp" => learned
                ? "A wrist wearable measures skin temperature, not core body temperature — this is "
                  + "not a fever reading. The dashed line is the device's own nightly baseline for "
                  + "this CardiMember, and what carries meaning is the distance from it rather than "
                  + "the number itself. There is no published range to shade behind it, because there "
                  + "is no population normal for a measurement this personal."
                : "A wrist wearable measures skin temperature, not core body temperature — this is "
                  + "not a fever reading. The device has not reported a nightly baseline for this "
                  + "CardiMember yet, so there is no dashed line to read these numbers against. "
                  + "There is no published range to shade behind them either, because there is no "
                  + "population normal for a measurement this personal.",

            "Blood Oxygen" => "The shaded band is the normal blood oxygen range"
                + $"{Published(metric, format, "%")}. CardiTrack has not learned a personal normal "
                + "for blood oxygen, so this chart has no dashed line — the readings are shown "
                + "against the published range alone.",

            "Breathing Rate" => "The shaded band is the normal adult breathing rate"
                + $"{Published(metric, format, "breaths per minute")}. CardiTrack has not learned a "
                + "personal normal for breathing rate, so this chart has no dashed line.",

            // A metric added to the carousel without copy gets the honest minimum rather than a
            // card that silently loses its panel.
            _ => learned
                ? "The dashed line is this CardiMember's own normal for this reading; a shaded band "
                  + "is the published typical-adult range."
                : "CardiTrack is still learning this CardiMember's own normal for this reading, so "
                  + "this chart has no dashed line yet; a shaded band is the published "
                  + "typical-adult range.",
        };
    }

    /// <summary>The band as the panel says it, spelled out with its unit and publisher.</summary>
    private static string Published(DashboardMetric metric, string format, string unit) =>
        metric.Reference is { } reference
            ? $" published by {reference.Source} ({string.Format(format, reference.Low)}–"
              + $"{string.Format(format, reference.High)} {unit})"
            : string.Empty;
}
