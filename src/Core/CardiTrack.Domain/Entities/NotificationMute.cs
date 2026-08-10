using CardiTrack.Domain.Common;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// A user's "don't ask again" — and the whole preference surface for the in-app engine, since
/// with no channels there is nothing else to configure.
/// </summary>
/// <remarks>
/// Scoped either to one rule or to a whole category, optionally narrowed to one CardiMember.
/// A mute suppresses <em>creation</em>, not just display: a muted gap produces no row at all, so
/// the inbox cannot accumulate invisible history the user can never see or clear.
/// </remarks>
public class NotificationMute : BaseEntity
{
    public Guid UserId { get; set; }

    /// <summary>Set for a single-rule mute; null when <see cref="Category"/> carries the scope.</summary>
    public string? RuleCode { get; set; }

    /// <summary>Set for a whole-category mute; null when <see cref="RuleCode"/> carries the scope.</summary>
    public NotificationCategory? Category { get; set; }

    /// <summary>Narrows the mute to one member. Null mutes the rule everywhere.</summary>
    public Guid? CardiMemberId { get; set; }

    public DateTime MutedDate { get; set; }

    /// <summary>Null means forever — until the user clears it from the settings screen.</summary>
    public DateTime? MutedUntil { get; set; }

    /// <summary>
    /// True only where a Safety-class rule was dismissed after the user confirmed what they were
    /// giving up. Recorded rather than prevented: silencing is theirs to choose, and the choice
    /// needs to be auditable.
    /// </summary>
    public bool AcknowledgedConsequence { get; set; }

    public NotificationMute()
    {
        MutedDate = DateTime.UtcNow;
    }

    public bool IsInEffect(DateTime utcNow) => MutedUntil is null || MutedUntil > utcNow;
}
