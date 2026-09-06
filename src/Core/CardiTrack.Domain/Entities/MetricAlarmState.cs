using CardiTrack.Domain.Common;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// Where one <see cref="MetricAlarm"/> stands for one CardiMember, and the reason this feature has
/// state at all.
/// <para>
/// Whether the M-of-N condition holds is recomputable from the readings on every tick, so it needs
/// no storage. The <em>transition</em> into <see cref="AlarmEvaluationState.Alarm"/> is not: without
/// the previous state, a five-minute cron watching a heart rate that stays high for an hour would
/// raise twelve identical alerts. Firing on entry and staying quiet until the alarm has returned to
/// <see cref="AlarmEvaluationState.Ok"/> is the same contract a cloud alarm offers, and it is what
/// re-arms this producer — the alert lifecycle does not, deliberately: a caregiver acknowledging a
/// card is saying they have read it, not that the heart rate has come down.
/// </para>
/// <para>
/// One row per (alarm, member): an account-level alarm is evaluated separately for every member
/// that inherits it, and each of them holds their own state.
/// </para>
/// </summary>
public class MetricAlarmState : BaseEntity
{
    public Guid MetricAlarmId { get; set; }

    public Guid CardiMemberId { get; set; }

    public AlarmEvaluationState State { get; set; } = AlarmEvaluationState.InsufficientData;

    /// <summary>When the alarm entered <see cref="State"/>. Survives ticks that do not change it.</summary>
    public DateTime StateSinceUtc { get; set; }

    /// <summary>The last tick that reached a verdict, for diagnosing an alarm that has gone quiet.</summary>
    public DateTime LastEvaluatedUtc { get; set; }

    /// <summary>The alert raised by the most recent transition into alarm, if any.</summary>
    public Guid? LastAlertId { get; set; }
}
