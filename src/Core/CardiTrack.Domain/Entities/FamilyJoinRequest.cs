using CardiTrack.Domain.Common;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// Somebody asking to be let into a family they know the Family ID of.
/// </summary>
/// <remarks>
/// <para>
/// The other half of <see cref="CaregiverInvite"/>, and the mirror image of it. An invitation is
/// pushed by an admin and carries its own grant; a join request is pulled by the asker and carries
/// none — what they get is decided by the admin at the moment of approval, not by whatever they
/// typed to get here.
/// </para>
/// <para>
/// <strong>The Family ID is an identifier, not a capability.</strong> It is short enough to read
/// down the phone, which means it is short enough to guess, and the product's answer to that is
/// not entropy but approval: knowing one buys the right to ask and nothing else. Every rule on
/// this entity follows from that. The request holds no grant. Creating one tells the asker nothing
/// about whether the family exists. And the row exists at all so an admin has something to approve
/// rather than a stranger arriving with access already in hand.
/// </para>
/// </remarks>
public class FamilyJoinRequest : BaseEntity
{
    /// <summary>The family being asked. Resolved from the Family ID the asker supplied.</summary>
    public Guid OrganizationId { get; set; }

    /// <summary>Who is asking. Always an authenticated account — nobody anonymous can ask.</summary>
    public Guid RequestedByUserId { get; set; }

    public FamilyJoinRequestStatus Status { get; set; } = FamilyJoinRequestStatus.Pending;

    /// <summary>
    /// When it stops being answerable. Past this instant the request is expired regardless of
    /// <see cref="Status"/>, for the same reason the invitations store expiry as a timestamp.
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>The admin who answered it, once somebody has.</summary>
    public Guid? ResolvedByUserId { get; set; }

    /// <summary>When it was answered, withdrawn, or otherwise finished.</summary>
    public DateTime? ResolvedAt { get; set; }
}
