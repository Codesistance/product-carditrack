using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.DTOs.Common;

/// <summary>What an alert-settings message asked for, in the closed vocabulary the planner may answer in.</summary>
public enum AlertChangeAction
{
    /// <summary>Which alerts and alarms are on for this member.</summary>
    List = 1,

    /// <summary>Switch one of CardiTrack's own rules on.</summary>
    EnableRule = 2,

    /// <summary>Switch one of CardiTrack's own rules off.</summary>
    DisableRule = 3,

    /// <summary>Add an alarm on a reading, for this member alone.</summary>
    CreateAlarm = 4,

    /// <summary>Retune or rename an alarm this member already has.</summary>
    EditAlarm = 5,

    /// <summary>Remove an alarm this member has.</summary>
    DeleteAlarm = 6,

    /// <summary>Switch an existing alarm on.</summary>
    EnableAlarm = 7,

    /// <summary>Switch an existing alarm off.</summary>
    DisableAlarm = 8,

    /// <summary>Quiet hours, muting, or how notifications are delivered — settings that live on
    /// the caregiver's own account, not on the member, and which chat points at rather than
    /// changes.</summary>
    NotificationSettings = 9,

    /// <summary>The message is about alerts but names nothing the vocabulary can resolve.</summary>
    Unclear = 10,
}

/// <summary>
/// The settings rung's plan: which rule or alarm a caregiver's message is about and what to do
/// with it, parsed defensively from the Rewrite slot's structured answer. Every field is a closed
/// label or a number; nothing the model wrote reaches a caregiver as text.
/// </summary>
/// <remarks>
/// <para>
/// The same shape discipline as <see cref="DataQueryPlan"/>: the type cannot name a person. The
/// member is the one the caregiver is chatting about, bound by the caller from trusted state, and
/// an existing alarm is named by the label the prompt assigned it (<c>alarm-1</c>), never by its
/// id — so a prompt-injected message cannot steer a change toward a row the caregiver was never
/// shown.
/// </para>
/// <para>
/// Alarm fields are all optional because a caregiver rarely says everything — "alert me if his
/// heart rate goes over 120" names a reading, a direction and a level, and nothing else. What
/// they left unsaid is filled by <see cref="Services.AlarmSuggestedDefaults"/> in code, and what
/// cannot be defaulted is asked for. A model that picks a label outside the closed set is
/// dropped to null, never coerced, the same as every other parse of model output here.
/// </para>
/// </remarks>
public sealed record AlertChangePlan
{
    public required AlertChangeAction Action { get; init; }

    /// <summary>One of <see cref="Services.AlertRuleCatalogue"/>'s ids, or null.</summary>
    public string? RuleId { get; init; }

    /// <summary>The prompt's label for an existing alarm (<c>alarm-1</c>…), or null.</summary>
    public string? AlarmLabel { get; init; }

    /// <summary>A name the caregiver gave a new or renamed alarm, or null for the default.</summary>
    public string? Name { get; init; }

    public AlarmMetric? Metric { get; init; }
    public AlarmStatistic? Statistic { get; init; }
    public AlarmOperator? Operator { get; init; }
    public AlarmThresholdKind? ThresholdKind { get; init; }
    public decimal? ThresholdValue { get; init; }
    public int? PeriodMinutes { get; init; }
    public int? EvaluationPeriods { get; init; }
    public int? DatapointsToAlarm { get; init; }
    public AlertSeverity? Severity { get; init; }
    public AlarmContextGate? ContextGate { get; init; }

    /// <summary>True when the action changes something rather than reads or redirects.</summary>
    public bool IsAChange => Action is not (AlertChangeAction.List
        or AlertChangeAction.NotificationSettings
        or AlertChangeAction.Unclear);
}
