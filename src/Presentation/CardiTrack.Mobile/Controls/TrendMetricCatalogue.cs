using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// Every metric the trends carousel can show, in the order it swipes: icon, the colour key of that
/// icon's own stroke, name, the format its headline reading takes, and the barer format its chart's
/// own min/max labels take.
/// </summary>
/// <remarks>
/// <para>
/// The ink is paired with the icon here rather than derived inside the card, because this is the
/// one place that already knows a metric is the steps one — matching them anywhere else would mean
/// a second table of metric identities to keep in step with this one.
/// </para>
/// <para>
/// Shared rather than private to Member Detail since the full-screen view opens the same metric
/// from the same table. Two tables would let the two views disagree about a metric's name or ink,
/// and the full-screen view is reached by tapping the card that would then be wrong.
/// </para>
/// </remarks>
public static class TrendMetricCatalogue
{
    public sealed record Entry(
        string Icon,
        string Ink,
        string Name,
        string Value,
        string Axis,
        Func<DashboardMetrics, DashboardMetric> Select);

    public static readonly IReadOnlyList<Entry> All =
    [
        new("icon_metric_steps.svg", "MetricStepsInk", "Activity", "{0:N0} steps", "{0:N0}", m => m.Steps),
        new("icon_metric_heart.svg", "MetricHeartInk", "Heart Rate", "{0:N0} bpm", "{0:N0}", m => m.RestingHeartRate),
        new("icon_metric_sleep.svg", "MetricSleepInk", "Sleep", "{0:0.#} hours", "{0:0.#}", m => m.Sleep),
        new("icon_metric_temperature.svg", "MetricTemperatureInk", "Skin Temp", "{0:0.#}°C", "{0:0.#}", m => m.Temperature),
        new("icon_metric_spo2.svg", "MetricSpO2Ink", "Blood Oxygen", "{0:0.#}%", "{0:0.#}", m => m.SpO2),
        new("icon_metric_breathing.svg", "MetricBreathingInk", "Breathing Rate", "{0:0.#} brpm", "{0:0.#}", m => m.BreathingRate),
    ];

    /// <summary>
    /// The entry a metric name belongs to, or null for a name this build does not carry. Null
    /// rather than a throw: the name arrives through a navigation route, and a route that has
    /// outlived the metric it names should land the caregiver on an empty screen they can leave,
    /// not take the app down.
    /// </summary>
    public static Entry? ByName(string? name) =>
        All.FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.Ordinal));
}
