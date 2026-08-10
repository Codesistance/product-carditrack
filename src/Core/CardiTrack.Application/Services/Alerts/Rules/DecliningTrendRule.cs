using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services.Alerts.Rules;

/// <summary>
/// Mobility falling steadily over a month — the slow decline nobody notices day to day.
/// </summary>
/// <remarks>
/// Compares the most recent fortnight against the one before it, both averaged, rather than
/// fitting a line: two weeks a side is enough to swamp a bad day, and the comparison is one a
/// caregiver can understand from the message alone.
/// </remarks>
public sealed class DecliningTrendRule : IAlertRule
{
    public const int WindowDays = 14;

    /// <summary>Fall between the two fortnights that counts as a decline rather than drift.</summary>
    public const decimal DeclineShare = 0.20m;

    /// <summary>Days needed in each fortnight before the comparison means anything.</summary>
    public const int MinimumDaysPerWindow = 8;

    public AlertType AlertType => AlertType.Trend;
    public AlertSeverity Severity => AlertSeverity.Orange;

    public AlertVerdict Evaluate(AlertContext context)
    {
        if (context.Baseline is null)
            return AlertVerdict.None;

        var withSteps = context.RecentDays.Where(d => d.Steps is not null).ToList();

        var recent = withSteps.TakeLast(WindowDays).ToList();
        var earlier = withSteps
            .SkipLast(WindowDays)
            .TakeLast(WindowDays)
            .ToList();

        if (recent.Count < MinimumDaysPerWindow || earlier.Count < MinimumDaysPerWindow)
            return AlertVerdict.None;

        var recentAvg = recent.Average(d => d.Steps!.Value);
        var earlierAvg = earlier.Average(d => d.Steps!.Value);

        if (earlierAvg <= 0)
            return AlertVerdict.None;

        var decline = (decimal)((earlierAvg - recentAvg) / earlierAvg);
        var threshold = DeclineShare * context.SensitivityFactor;

        if (decline < threshold)
            return AlertVerdict.None;

        return AlertVerdict.Raise(
            "Gradually less active",
            $"Activity has fallen about {decline:P0} over the past month — "
            + $"from around {earlierAvg:N0} steps a day to {recentAvg:N0}. "
            + "Gradual changes like this are easy to miss up close.",
            new Dictionary<string, object>
            {
                ["recentAverage"] = (int)recentAvg,
                ["earlierAverage"] = (int)earlierAvg,
                ["declinePercent"] = Math.Round(decline * 100, 1)
            });
    }
}
