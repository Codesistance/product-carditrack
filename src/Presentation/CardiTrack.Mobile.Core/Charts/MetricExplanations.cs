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
/// non-clinical reader which of the two to read a night against, or that a wrist wearable is not
/// taking anyone's temperature.
/// </para>
/// <para>
/// For sleep, resting heart rate and blood oxygen the published range is the normal (decision
/// 2026-09-25, <see cref="MetricReference.IsPublishedNormal"/>): a reading outside it is worth a
/// look even when it is usual for this member, and their usual is secondary context. The copy for
/// those three says so, and calls the member's own figure "their usual" so that "normal" names one
/// thing on the card.
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
    /// than assumed. "Readings worth a look" rather than "changes from someone's own normal": for
    /// sleep, resting heart rate and blood oxygen the published range is now the normal, and a
    /// panel that has just said so should not close by saying the opposite.
    /// </summary>
    private const string NotADiagnosis =
        "CardiTrack watches for readings worth a look. It doesn't diagnose.";

    /// <summary>
    /// The line under the chart, and the panel behind the "i". <paramref name="memberFirstName"/>
    /// is who the chart is about — the copy names them ("Dad's own usual step count") rather than
    /// reaching for a label, and falls back to "their"/"them" when no name is on file.
    /// </summary>
    /// <param name="window">
    /// The points the chart on screen actually draws — a 7-day window is the tail of a longer
    /// series. The panel only explains a mark that window carries; null reads the whole series.
    /// </param>
    public static (string Footer, string Panel) For(
        string name, DashboardMetric metric, string format, string? memberFirstName = null,
        IReadOnlyList<MetricPoint>? window = null)
    {
        var panel = Panel(name, metric, format, memberFirstName, window ?? metric.Series);
        return (Footer(name, metric, format), $"{panel}\n\n{NotADiagnosis}");
    }

    /// <summary>Possessive form for mid-sentence use: "Dad's" or "their".</summary>
    private static string Who(string? first) =>
        string.IsNullOrWhiteSpace(first) ? "their" : $"{first}'s";

    /// <summary>Object form for mid-sentence use: "Dad" or "them".</summary>
    private static string WhoPlain(string? first) =>
        string.IsNullOrWhiteSpace(first) ? "them" : first;

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
            // Not "their own normal": the published range is the normal for resting heart rate.
            "Heart Rate" => "their usual",
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
        {
            var range = $"{string.Format(format, reference.Low)}–{string.Format(format, reference.High)} "
                        + $"({reference.Source}).";

            // Where the band is the normal it is named as such and named first, the order the
            // chart now draws them in.
            if (reference.IsPublishedNormal)
                parts.Insert(0, $"Normal range {range}");
            else
                parts.Add($"Band {range}");
        }

        return string.Join(" ", parts);
    }

    /// <summary>
    /// The fuller account behind the "i". Each metric answers the same two questions — what the
    /// dashed rule is, and what the band is — in whichever form matches what is on the chart.
    /// </summary>
    private static string Panel(
        string name, DashboardMetric metric, string format, string? who, IReadOnlyList<MetricPoint> window)
    {
        var learned = metric.Baseline is not null;

        return name switch
        {
            "Activity" => learned
                ? $"The dashed line is {Who(who)} own usual step count, learned from their own "
                  + "history — it is not a target. No health body publishes a step count to compare "
                  + "anyone against, so this chart has no shaded band. A quieter day than usual is "
                  + "worth noticing, not worrying about."
                : $"CardiTrack is still learning {Who(who)} usual step count from their own "
                  + "history, so there is no dashed line on this chart yet. No health body publishes "
                  + "a step count to compare anyone against either, so there is no shaded band. Until "
                  + "their usual day is known, the shape of the line is the thing to read.",

            // The band first, because it is the normal (decision 2026-09-25) — this panel used to
            // tell caregivers the opposite: that a rate outside it could be "perfectly ordinary".
            "Heart Rate" => $"The shaded band is the normal adult resting heart rate"
                + $"{Published(metric, format, "bpm")}. A resting rate outside it is worth a look, "
                + $"even when it is usual for {WhoPlain(who)}. "
                + (learned
                    ? $"The dashed line is {Who(who)} usual resting rate, learned over time — it "
                      + "shows whether a reading is new for them, not whether it is normal."
                    : $"CardiTrack is still learning {Who(who)} usual resting rate, so there is no "
                      + "dashed line on this chart yet. Once it is known, it will show whether a "
                      + "reading is new for them."),

            "Sleep" => "The shaded band is the nightly sleep recommended for their age group"
                + $"{Published(metric, format, "hours")} — the recommendation drops by an hour from "
                + "age 65, so it is drawn for their age rather than as a single adult figure. A night "
                + $"outside it is worth a look, even when it is usual for {WhoPlain(who)}. "
                + (learned
                    ? $"The dashed line is {Who(who)} usual night, which shows whether a night is "
                      + "new for them. "
                    : $"CardiTrack is still learning {Who(who)} usual night, so there is no dashed "
                      + "line on this chart yet. ")
                + (window.Any(NightReading.IsAwake)
                    ? "A diamond marks a night the watch was worn with no sleep recorded — "
                      + "counted as awake all night. "
                    : string.Empty)
                + "This measures how long they slept, not how well.",

            "Skin Temp" => learned
                ? "A wrist wearable measures skin temperature, not core body temperature — this is "
                  + "not a fever reading. The dashed line is the device's own nightly baseline for "
                  + $"{WhoPlain(who)}, and what carries meaning is the distance from it rather than "
                  + "the number itself. There is no published range to shade behind it, because there "
                  + "is no population normal for a measurement this personal."
                : "A wrist wearable measures skin temperature, not core body temperature — this is "
                  + "not a fever reading. The device has not reported a nightly baseline for "
                  + $"{WhoPlain(who)} yet, so there is no dashed line to read these numbers against. "
                  + "There is no published range to shade behind them either, because there is no "
                  + "population normal for a measurement this personal.",

            "Blood Oxygen" => "The shaded band is the normal blood oxygen range"
                + $"{Published(metric, format, "%")}, and a reading below it is worth a look. "
                + "CardiTrack has not learned a personal normal for blood oxygen, so this chart has "
                + "no dashed line — the readings are shown against the published range alone.",

            "Breathing Rate" => "The shaded band is the normal adult breathing rate"
                + $"{Published(metric, format, "breaths per minute")}. CardiTrack has not learned a "
                + "personal normal for breathing rate, so this chart has no dashed line.",

            // A metric added to the carousel without copy gets the honest minimum rather than a
            // card that silently loses its panel.
            _ => learned
                ? $"The dashed line is {Who(who)} own normal for this reading; a shaded band "
                  + "is the published typical-adult range."
                : $"CardiTrack is still learning {Who(who)} own normal for this reading, so "
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
