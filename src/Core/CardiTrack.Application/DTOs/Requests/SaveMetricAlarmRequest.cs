using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.DTOs.Requests;

/// <summary>
/// Creates or replaces one alarm. The same shape for an account-level default and a member row —
/// scope comes from the route, not the body, so a client cannot move an alarm between scopes by
/// editing a field.
/// </summary>
public sealed class SaveMetricAlarmRequest
{
    public string Name { get; set; } = string.Empty;

    public AlarmMetric Metric { get; set; }
    public AlarmStatistic Statistic { get; set; }
    public AlarmOperator Operator { get; set; }
    public AlarmThresholdKind ThresholdKind { get; set; }
    public decimal ThresholdValue { get; set; }
    public int PeriodMinutes { get; set; }
    public int EvaluationPeriods { get; set; } = 1;
    public int DatapointsToAlarm { get; set; } = 1;
    public AlarmMissingDataTreatment MissingDataTreatment { get; set; } = AlarmMissingDataTreatment.Missing;
    public AlertSeverity Severity { get; set; } = AlertSeverity.Yellow;
    public AlarmContextGate ContextGate { get; set; } = AlarmContextGate.None;
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Required to save an alarm at <see cref="AlertSeverity.Red"/>. Red pushes, pierces quiet
    /// hours and escalates to other caregivers when unacknowledged — an explicit acknowledgement
    /// that this is what the caregiver means keeps a mis-tapped severity from waking a family at
    /// three in the morning.
    /// </summary>
    public bool ConfirmCriticalSeverity { get; set; }
}
