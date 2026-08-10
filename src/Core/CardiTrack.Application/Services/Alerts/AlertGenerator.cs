using System.Text.Json;
using CardiTrack.Application.Services.Alerts.Rules;
using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Services.Alerts;

/// <summary>
/// Runs the statistical rule set over one member and returns the alerts that should be raised.
/// </summary>
/// <remarks>
/// Pure and deterministic — it takes a snapshot and returns entities, writing nothing — so the
/// suppression rules below are unit tests rather than hopes.
/// </remarks>
public static class AlertGenerator
{
    /// <summary>
    /// The five launch types, in the order a caregiver would want them: the one meaning "go and
    /// check" first, the slow-burn trend last.
    /// </summary>
    public static IReadOnlyList<IAlertRule> Rules { get; } =
    [
        new NoMorningActivityRule(),
        new ElevatedHeartRateRule(),
        new InactivityRule(),
        new IrregularSleepRule(),
        new DecliningTrendRule()
    ];

    public static IReadOnlyList<Alert> Generate(
        AlertContext context, IReadOnlyList<IAlertRule>? rules = null)
    {
        // Somebody deliberately paused monitoring. Alerting anyway would make the pause a lie.
        if (context.IsMonitoringPaused)
            return [];

        // Nothing may alert off a provisional or absent baseline. A member still learning has no
        // established "normal" to deviate from, and the first thing a new caregiver experiences
        // must not be a false alarm.
        if (context.Baseline is null)
            return [];

        var raised = new List<Alert>();

        foreach (var rule in rules ?? Rules)
        {
            // One open alert per type is enough. A condition true for five days running is one
            // thing happening, not five — and five identical rows is how an alert list becomes
            // something a caregiver scrolls past.
            if (context.OpenAlertTypes.Contains(rule.AlertType))
                continue;

            var verdict = rule.Evaluate(context);
            if (!verdict.ShouldAlert)
                continue;

            raised.Add(new Alert
            {
                CardiMemberId = context.CardiMemberId,
                AlertType = rule.AlertType,
                Severity = rule.Severity,
                Title = verdict.Title,
                Message = verdict.Message,
                TriggeredDate = context.UtcNow,
                MetricValues = verdict.MetricValues.Count == 0
                    ? null
                    : JsonSerializer.Serialize(verdict.MetricValues),
                IsResolved = false,
                IsActive = true
            });
        }

        return raised;
    }
}
