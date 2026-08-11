using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services.Notifications;

/// <summary>
/// The escalation ladder for Safety and red Health deliveries — push, re-push at t+120s, fan out
/// at t+300s, page ops at t+900s (notification_engine.md §6.3). Pure over
/// <see cref="EscalationContext"/>: no I/O, no clock call, so the 120s/300s/900s boundaries are
/// table-tested exactly like the nudge rule catalogue.
/// </summary>
public static class EscalationPolicy
{
    public static readonly TimeSpan RepushAfter = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan FanOutAfter = TimeSpan.FromSeconds(300);
    public static readonly TimeSpan UndeliveredAfter = TimeSpan.FromSeconds(900);

    /// <summary>
    /// The next action given how long the delivery has been outstanding. An ack at any stage
    /// halts the ladder before this is ever called again — the caller checks
    /// <see cref="Domain.Entities.NotificationDelivery.DeliveredDate"/> first, not this method.
    /// </summary>
    public static EscalationAction Evaluate(EscalationContext context)
    {
        if (!context.Escalates || context.SentDate is null)
            return EscalationAction.None;

        var elapsed = context.UtcNow - context.SentDate.Value;

        return (context.CurrentStage, elapsed) switch
        {
            (EscalationStage.Initial, var e) when e >= UndeliveredAfter => EscalationAction.MarkUndeliveredCritical,
            (EscalationStage.Initial, var e) when e >= FanOutAfter => EscalationAction.FanOutToOtherCaregivers,
            (EscalationStage.Initial, var e) when e >= RepushAfter => EscalationAction.Repush,

            (EscalationStage.Repushed, var e) when e >= UndeliveredAfter => EscalationAction.MarkUndeliveredCritical,
            (EscalationStage.Repushed, var e) when e >= FanOutAfter => EscalationAction.FanOutToOtherCaregivers,

            (EscalationStage.FannedOut, var e) when e >= UndeliveredAfter => EscalationAction.MarkUndeliveredCritical,

            _ => EscalationAction.None
        };
    }
}

public enum EscalationAction
{
    None,
    Repush,

    /// <summary>
    /// Copy every other caregiver with <c>ReceiveAlerts</c> on. In R1 (<c>MaxUsers = 1</c>) this
    /// finds zero secondary caregivers and falls straight through — the branch still runs, rather
    /// than being special-cased away, so nothing needs removing when family invitations (R3) add
    /// real targets. The fan-out copy never names who failed to respond (§6.3) — that is a caller
    /// concern (rendering), not this policy's.
    /// </summary>
    FanOutToOtherCaregivers,
    MarkUndeliveredCritical
}

public sealed record EscalationContext
{
    public required DateTime UtcNow { get; init; }
    public required bool Escalates { get; init; }
    public required EscalationStage CurrentStage { get; init; }
    public DateTime? SentDate { get; init; }
}
