using CardiTrack.Domain.Common;
using CardiTrack.Domain.Enums;
using CardiTrack.Domain.Interfaces;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// A caregiver-defined alarm: "tell me when this metric reaches this level". The grammar is the one
/// cloud monitoring made standard — metric, statistic, comparison, threshold, evaluation window,
/// and how many datapoints inside that window have to breach before it counts.
/// <para>
/// <b>Scope lives in <see cref="CardiMemberId"/>.</b> Null means the row is an account-level
/// default that every CardiMember in the organization inherits; set means the row applies to that
/// member alone. A member row that names an account row in <see cref="DerivedFromAlarmId"/>
/// <em>replaces</em> it for that member — and replacing it with <see cref="IsEnabled"/> false is
/// how a member opts out of an inherited alarm. A member row naming nothing is an addition.
/// </para>
/// <para>
/// Alarms defined here coexist with the nine built-in statistical rules rather than replacing them:
/// those stay as shipped presets, toggled per member through <see cref="AlertPreference"/>. The two
/// mechanisms are deliberately separate because a built-in rule is keyed by a compile-time
/// catalogue string and an alarm is keyed by a Guid, and <c>AlertRuleOverrides</c> drops ids its
/// catalogue does not know.
/// </para>
/// </summary>
public class MetricAlarm : BaseEntity, ISoftDeletable
{
    /// <summary>The owning organization. Always set — it is the tenancy boundary, and an alarm is
    /// never resolved for a member outside it.</summary>
    public Guid OrganizationId { get; set; }

    /// <summary>Null = an account-level default inherited by every member; set = this member only.</summary>
    public Guid? CardiMemberId { get; set; }

    /// <summary>The account-level alarm this member row overrides, if any.</summary>
    public Guid? DerivedFromAlarmId { get; set; }

    /// <summary>The caregiver's own label for the alarm, shown on the card and in the alert.</summary>
    public string Name { get; set; } = string.Empty;

    public AlarmMetric Metric { get; set; }

    public AlarmStatistic Statistic { get; set; }

    public AlarmOperator Operator { get; set; }

    public AlarmThresholdKind ThresholdKind { get; set; }

    /// <summary>The stored number, read according to <see cref="ThresholdKind"/>.</summary>
    public decimal ThresholdValue { get; set; }

    /// <summary>
    /// How long one datapoint covers. Clamped by the catalogue to a small set of values, floored at
    /// five minutes: ingestion polls every ten minutes, so an alarm cannot be more responsive than
    /// the data reaching us, and offering a one-minute period would promise a latency we do not have.
    /// A daily metric uses 1440 and a datapoint is one civil day.
    /// </summary>
    public int PeriodMinutes { get; set; }

    /// <summary>How many periods the evaluation range spans — CloudWatch's N.</summary>
    public int EvaluationPeriods { get; set; }

    /// <summary>
    /// How many of those periods must breach before the alarm fires — CloudWatch's M. The breaching
    /// periods need not be consecutive: two bad readings either side of a good one inside ten
    /// minutes is the signal, and demanding they be adjacent would miss it.
    /// </summary>
    public int DatapointsToAlarm { get; set; }

    public AlarmMissingDataTreatment MissingDataTreatment { get; set; } = AlarmMissingDataTreatment.Missing;

    /// <summary>The severity the raised <see cref="Alert"/> carries, which is what decides whether
    /// it pushes, pierces quiet hours, and escalates.</summary>
    public AlertSeverity Severity { get; set; } = AlertSeverity.Yellow;

    public AlarmContextGate ContextGate { get; set; } = AlarmContextGate.None;

    /// <summary>Off means the alarm is not evaluated at all — no row is written, nothing is
    /// suppressed after the fact.</summary>
    public bool IsEnabled { get; set; } = true;

    public bool IsActive { get; set; } = true;
}
