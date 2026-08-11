using System.ComponentModel.DataAnnotations;

namespace CardiTrack.Domain.Enums;

/// <summary>
/// Lifecycle of one outbox row (notification_engine.md §6.2, §8).
/// </summary>
public enum DeliveryState
{
    /// <summary>Written, not yet attempted or waiting on <c>ScheduledFor</c> (quiet-hours deferral).</summary>
    [Display(Name = "Pending")]
    Pending = 1,

    /// <summary>Accepted by the provider. Not proof of arrival — see <see cref="Delivered"/>.</summary>
    [Display(Name = "Sent")]
    Sent = 2,

    /// <summary>Client posted <c>/delivered</c>. Halts escalation.</summary>
    [Display(Name = "Delivered")]
    Delivered = 3,

    /// <summary>The explicit collapse rule at send time dropped it — never a unique-constraint side effect.</summary>
    [Display(Name = "Suppressed")]
    Suppressed = 4,

    /// <summary>A <c>Retryable</c> send outcome, still within the retry/backoff window.</summary>
    [Display(Name = "Failed")]
    Failed = 5,

    /// <summary>Retries exhausted before the message TTL. Terminal.</summary>
    [Display(Name = "DeadLettered")]
    DeadLettered = 6,

    /// <summary>Escalation ladder ran out with no ack from anyone. Terminal, paged.</summary>
    [Display(Name = "Undelivered")]
    Undelivered = 7
}
