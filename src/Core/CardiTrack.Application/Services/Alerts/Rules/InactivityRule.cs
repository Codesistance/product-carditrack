using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services.Alerts.Rules;

/// <summary>
/// Steps well below the member's own normal for two days running.
/// </summary>
/// <remarks>
/// Two consecutive days rather than one: a single quiet day is a rainy Sunday, and alerting on it
/// is how a caregiver learns to ignore the app. Two is where a pattern starts.
/// </remarks>
public sealed class InactivityRule : IAlertRule
{
    /// <summary>Below this share of baseline steps a day counts as inactive.</summary>
    public const decimal InactiveShareOfBaseline = 0.50m;

    public const int ConsecutiveDaysRequired = 2;

    public AlertType AlertType => AlertType.Inactivity;
    public AlertSeverity Severity => AlertSeverity.Yellow;

    public AlertVerdict Evaluate(AlertContext context)
    {
        var baseline = context.Baseline?.AvgSteps;
        if (baseline is null or <= 0)
            return AlertVerdict.None;

        // A higher sensitivity trips at a smaller drop, so the threshold rises toward baseline.
        var threshold = baseline.Value * InactiveShareOfBaseline / context.SensitivityFactor;

        var recent = context.RecentDays
            .Where(d => d.Steps is not null)
            .TakeLast(ConsecutiveDaysRequired)
            .ToList();

        // Missing days are not quiet days — a watch left on charge would otherwise read as a
        // person who stopped moving, which is the most alarming thing we could get wrong.
        if (recent.Count < ConsecutiveDaysRequired)
            return AlertVerdict.None;

        if (recent.Any(d => d.Steps >= threshold))
            return AlertVerdict.None;

        var average = (int)recent.Average(d => d.Steps!.Value);

        return AlertVerdict.Raise(
            "Much less active than usual",
            $"Activity has been well below normal for {ConsecutiveDaysRequired} days — "
            + $"around {average:N0} steps a day against a usual {baseline.Value:N0}. "
            + "It may be worth a call.",
            new Dictionary<string, object>
            {
                ["steps"] = average,
                ["baseline"] = baseline.Value,
                ["days"] = recent.Count
            });
    }
}
