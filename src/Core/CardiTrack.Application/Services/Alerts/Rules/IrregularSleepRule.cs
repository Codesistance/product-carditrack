using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services.Alerts.Rules;

/// <summary>
/// Sleep efficiency poor for most of a working week.
/// </summary>
/// <remarks>
/// Five days, and measured against a fixed 70% floor rather than the member's own baseline:
/// efficiency is already a normalised percentage, and someone whose normal is poor sleep should
/// still have that noticed rather than normalised away.
/// </remarks>
public sealed class IrregularSleepRule : IAlertRule
{
    /// <summary>Below this percentage a night counts as poor.</summary>
    public const int PoorEfficiencyPercent = 70;

    public const int ConsecutiveDaysRequired = 5;

    public AlertType AlertType => AlertType.Sleep;
    public AlertSeverity Severity => AlertSeverity.Yellow;

    public AlertVerdict Evaluate(AlertContext context)
    {
        // Gated on a sleep baseline existing, which is how we know the member wears the watch
        // overnight at all. Without it, five missing nights would look like five bad ones.
        if (context.Baseline?.AvgSleepEfficiency is null)
            return AlertVerdict.None;

        var threshold = PoorEfficiencyPercent / context.SensitivityFactor;

        var recent = context.RecentDays
            .Where(d => d.SleepEfficiency is not null)
            .TakeLast(ConsecutiveDaysRequired)
            .ToList();

        if (recent.Count < ConsecutiveDaysRequired)
            return AlertVerdict.None;

        if (recent.Any(d => d.SleepEfficiency >= threshold))
            return AlertVerdict.None;

        var average = (int)recent.Average(d => d.SleepEfficiency!.Value);

        return AlertVerdict.Raise(
            "Sleeping poorly this week",
            $"Sleep has been broken for {ConsecutiveDaysRequired} nights running — "
            + $"about {average}% of time in bed actually asleep. "
            + "A quick chat about how they're sleeping might help.",
            new Dictionary<string, object>
            {
                ["sleepEfficiency"] = average,
                ["threshold"] = PoorEfficiencyPercent,
                ["nights"] = recent.Count
            });
    }
}
