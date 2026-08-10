using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services.Alerts.Rules;

/// <summary>
/// Resting heart rate persistently above the member's own normal.
/// </summary>
/// <remarks>
/// Three consecutive days, because resting heart rate moves with heat, a late night or a glass of
/// wine. A sustained lift is the signal; a single elevated morning is noise.
/// </remarks>
public sealed class ElevatedHeartRateRule : IAlertRule
{
    /// <summary>How far above baseline resting HR must sit to count as elevated.</summary>
    public const decimal ElevatedMargin = 0.15m;

    public const int ConsecutiveDaysRequired = 3;

    public AlertType AlertType => AlertType.HeartRate;
    public AlertSeverity Severity => AlertSeverity.Orange;

    public AlertVerdict Evaluate(AlertContext context)
    {
        var baseline = context.Baseline?.AvgRestingHeartRate;
        if (baseline is null or <= 0)
            return AlertVerdict.None;

        var margin = ElevatedMargin * context.SensitivityFactor;
        var threshold = baseline.Value * (1 + margin);

        var recent = context.RecentDays
            .Where(d => d.RestingHeartRate is not null)
            .TakeLast(ConsecutiveDaysRequired)
            .ToList();

        if (recent.Count < ConsecutiveDaysRequired)
            return AlertVerdict.None;

        if (recent.Any(d => d.RestingHeartRate <= threshold))
            return AlertVerdict.None;

        var average = (int)recent.Average(d => d.RestingHeartRate!.Value);

        return AlertVerdict.Raise(
            "Resting heart rate is up",
            $"Resting heart rate has been above normal for {ConsecutiveDaysRequired} days — "
            + $"around {average} bpm against a usual {baseline.Value} bpm. "
            + "Worth mentioning at the next doctor's visit.",
            new Dictionary<string, object>
            {
                ["restingHeartRate"] = average,
                ["baseline"] = baseline.Value,
                ["days"] = recent.Count
            });
    }
}
