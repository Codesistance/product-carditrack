// CardiTrack.Application shadows MAUI's Application in any file importing it — see NudgeMiniRow.
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
