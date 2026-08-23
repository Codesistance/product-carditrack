// CardiTrack.Application shadows MAUI's Application in any file importing it — see NudgeMiniRow.
using CardiTrack.Application.DTOs.Responses;
using MauiApplication = Microsoft.Maui.Controls.Application;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// The one reading of a metric's green/yellow/orange/red status string — its accent colour and its
/// pill wording. Shared so the dashboard's <see cref="MetricCard"/> and the Member Detail screen's
/// <see cref="MetricTrendCard"/> cannot drift into describing the same status differently.
/// </summary>
internal static class MetricStatus
{
    /// <summary>
    /// Pill tint/ink resource keys and wording, or null for a status that carries no judgement
    /// (a metric with no baseline to compare against), where no pill is shown at all.
    /// </summary>
    /// <remarks>
    /// The wording is deliberately non-clinical — CardiTrack is not a medical device, so a pill
    /// reports how a reading compares with this member's own baseline rather than naming it high
    /// or low.
    /// </remarks>
    public static (string Tint, string Ink, string Text)? Pill(string status) => status switch
    {
        "green" => ("PillGreenBackground", "StatusGreen", "NORMAL"),
        "yellow" => ("PillYellowBackground", "StatusYellow", "UNUSUAL"),
        "orange" => ("PillOrangeBackground", "StatusOrange", "CHECK IN"),
        "red" => ("PillRedBackground", "StatusRed", "URGENT"),
        _ => null,
    };

    /// <summary>
    /// The pill for a metric with no learned baseline, judged instead against the published
    /// reference range the payload carries (SpO2 and breathing rate, whose bands are WHO's — see
    /// <c>HealthReferenceRanges</c>). Inside the band is NORMAL, outside it UNUSUAL — the same
    /// two words the baseline pill uses, because the caption beside it names the band and its
    /// publisher, which is what tells the reader which comparison was made.
    /// </summary>
    /// <remarks>
    /// Only the band's two sides, never CHECK IN or URGENT: a severity grading against a
    /// population range is a clinical judgement this card must not make. How far outside the
    /// band matters is the alert pipeline's question, and it answers on the alerts tab.
    /// </remarks>
    public static (string Tint, string Ink, string Text)? ReferencePill(decimal? value, MetricReference? reference) =>
        value is not { } v || reference is null
            ? null
            : Pill(v >= reference.Low && v <= reference.High ? "green" : "yellow");

    /// <summary>
    /// The sleep card's pill: one word naming the band of the night's rating (the API's
    /// <c>DashboardMetric.QualityScore</c>) — never the metric's status, which for sleep reads
    /// duration against the baseline alone and is allowed to disagree with the rating (a
    /// habitually short sleeper's night is normal for them and still not enough sleep). The
    /// bands are the same ones the star row colours itself by, so the pill and the stars on
    /// this card agree by construction: 3-5 is GOOD/green, 2 FAIR/yellow, 1 POOR/orange. Null
    /// when the night is unrated, which hides the pill along with the stars.
    /// </summary>
    /// <remarks>
    /// A quality vocabulary rather than the comparison one above, because the rating is not
    /// purely member-relative: NORMAL/UNUSUAL describe how a reading sits against the member's
    /// own baseline, and a 4.5-hour night is entirely usual for someone who always sleeps 4.5
    /// hours — the pill would be lying. GOOD/FAIR/POOR says only how the night rates, which is
    /// exactly what the stars beside it say.
    /// </remarks>
    public static (string Tint, string Ink, string Text)? SleepQualityPill(int? qualityScore) => qualityScore switch
    {
        null => null,
        >= 3 => ("PillGreenBackground", "StatusGreen", "GOOD"),
        2 => ("PillYellowBackground", "StatusYellow", "FAIR"),
        _ => ("PillOrangeBackground", "StatusOrange", "POOR"),
    };

    /// <summary>The colour a trend line or accent takes for this status.</summary>
    public static Color Accent(string status) => Resource(status switch
    {
        "green" => "StatusGreen",
        "yellow" => "StatusYellow",
        "orange" => "StatusOrange",
        "red" => "StatusRed",
        _ => "StatusUnknown",
    }, Colors.Gray);

    public static Color Resource(string key, Color fallback) =>
        MauiApplication.Current?.Resources.TryGetValue(key, out var value) == true && value is Color colour
            ? colour
            : fallback;
}
