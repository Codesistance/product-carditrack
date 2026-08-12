using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Application.Services;

/// <summary>
/// The published typical-adult range for each Key Metric — the population normal a client draws
/// behind the series next to this member's own learned baseline
/// (<see cref="DashboardMetric.Baseline"/>).
/// </summary>
/// <remarks>
/// <para>
/// Every range names the body that publishes it, because they do not all come from one. WHO
/// publishes the two it is quoted for here — the SpO2 bands in its pulse oximetry guidance and the
/// adult respiratory rate in its Basic Emergency Care material — but publishes no resting heart
/// rate or sleep duration range, so those are attributed to the bodies that do rather than
/// re-labelled WHO.
/// </para>
/// <para>
/// A metric with no published range gets none: skin temperature is a wearer-relative measurement
/// with no population normal (it compares against the device's own nightly baseline instead), and
/// no standards body publishes a daily step count — the WHO physical activity guidelines are
/// written in minutes of moderate activity per week, and converting those to steps would be our
/// arithmetic wearing WHO's name. Steps keep their goal and baseline, which are this member's own.
/// </para>
/// <para>
/// General adult ranges, not adjusted for age or sex. The bands that shift with age shift little
/// across the population CardiTrack monitors (the 65+ sleep recommendation is 7–8 hours against
/// 7–9 for younger adults), and a range drawn as background context does not carry the precision
/// that per-member tailoring would imply.
/// </para>
/// </remarks>
public static class HealthReferenceRanges
{
    /// <summary>Normal adult resting heart rate, 60–100 bpm (American Heart Association).</summary>
    public static MetricReference RestingHeartRate => new() { Low = 60m, High = 100m, Source = "AHA" };

    /// <summary>Recommended nightly sleep for adults, 7–9 hours (National Sleep Foundation).</summary>
    public static MetricReference Sleep => new() { Low = 7m, High = 9m, Source = "NSF" };

    /// <summary>
    /// Normal blood oxygen saturation, 94–100% at sea level (WHO pulse oximetry guidance, which
    /// puts 90–93% at hypoxaemia and below 90% at severe hypoxaemia).
    /// </summary>
    public static MetricReference SpO2 => new() { Low = 94m, High = 100m, Source = "WHO" };

    /// <summary>Normal adult respiratory rate, 12–20 breaths per minute (WHO Basic Emergency Care).</summary>
    public static MetricReference BreathingRate => new() { Low = 12m, High = 20m, Source = "WHO" };
}
