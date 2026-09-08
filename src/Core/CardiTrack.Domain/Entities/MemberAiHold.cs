using CardiTrack.Domain.Common;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// A member whose prompt the medical model could not finish, and until when the generation path
/// that found out should stop asking. One row per member per <see cref="AiHoldPurpose"/>.
/// </summary>
/// <remarks>
/// <para>
/// Exists because a structured reply that runs to the output ceiling is a model looping inside
/// the reply grammar, and the same prompt loops the same way on the next pass. Without a record
/// of that, every pass — the half-hourly digest job and the assessor re-running it after each
/// upload — spent the full ceiling's worth of GPU time on the same member to get nothing, and
/// the failure log filled with the same line every few minutes.
/// </para>
/// <para>
/// A hold is about the model and the prompt, not about the member's readings: it is not waived
/// by an alert or a jump the way the digest's regeneration floor is, because the read that
/// would describe the alert is the read that cannot finish. It is cleared by the first reply
/// that does finish, so a member is never held a moment longer than the model's own behaviour
/// requires. Held rows carry no health data — a purpose, a count and two timestamps.
/// </para>
/// </remarks>
public class MemberAiHold : BaseEntity
{
    public Guid CardiMemberId { get; set; }

    public AiHoldPurpose Purpose { get; set; }

    /// <summary>The generation path skips this member until this instant.</summary>
    public DateTime HeldUntilUtc { get; set; }

    /// <summary>When the most recent unfinished reply was recorded.</summary>
    public DateTime LastFailedAtUtc { get; set; }

    /// <summary>
    /// Unfinished replies in a row, without a finished one between them. The hold lengthens
    /// with it, so a member the model cannot read at all costs a couple of calls a day rather
    /// than one per pass.
    /// </summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>A short, payload-free label for what went wrong — <c>truncated</c> today.</summary>
    public string Reason { get; set; } = string.Empty;
}
