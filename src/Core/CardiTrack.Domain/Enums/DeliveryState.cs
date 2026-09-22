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

    /// <summary>
    /// Deliberately not sent, and not a failure: the explicit collapse rule at send time dropped
    /// it — never a unique-constraint side effect — or the recipient stopped being one, which
    /// today means they asked for their account to be deleted. Terminal, and distinct from
    /// <see cref="DeadLettered"/>, where something was tried and did not work.
    /// </summary>
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
    Undelivered = 7,

    /// <summary>
    /// A caregiver answered the alert this delivery was about — in the app, on some device, not
    /// necessarily this one. Terminal, and it halts escalation.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Delivered"/> on purpose, although both stop the ladder.
    /// <see cref="Delivered"/> means a specific handset posted <c>/delivered</c>, and the
    /// time-to-ack SLO is measured from exactly those; counting an in-app answer as one would
    /// quietly report the push as having arrived on a phone that may have been face-down all
    /// night. What is true here is the thing that actually matters — somebody dealt with it — and
    /// saying only that is what keeps the delivery metric about deliveries.
    /// </remarks>
    [Display(Name = "Answered")]
    Answered = 8
}
