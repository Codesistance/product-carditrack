using CardiTrack.Domain.Common;
using CardiTrack.Domain.Enums;
using CardiTrack.Domain.Interfaces;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// One person's membership of one family — the relation that lets a caregiver belong to their
/// sibling's family without giving up their own.
/// </summary>
/// <remarks>
/// <para>
/// Until this existed a user belonged to exactly one organization, through
/// <see cref="User.OrganizationId"/>. That column stays, and now means something narrower: the
/// family this person <em>created</em> and pays for, or null for a guest who only ever joined
/// someone else's. Everything that is about the home family — the subscription, the account-level
/// alarm defaults, the organisation's own erasure — keeps reading it. Everything that is about
/// <em>which families this person is in</em> reads these rows instead.
/// </para>
/// <para>
/// <see cref="Role"/> lives here rather than on the user because a role is held in a family, not
/// by a person: the same caregiver is the Admin of the family they started and a Member of the
/// one they were let into. <see cref="User.Role"/> remains the account-level value the request
/// context carries and is not what decides who may invite or approve.
/// </para>
/// <para>
/// Deliberately bare — two ids, a role and timestamps, with no navigation properties. The rest of
/// the schema carries almost no foreign keys by design (see data_protection_architecture.md), and
/// a membership row must be deletable in either direction of an erasure without a constraint
/// deciding the order for it.
/// </para>
/// </remarks>
public class UserOrganization : BaseEntity, ISoftDeletable
{
    public Guid UserId { get; set; }

    public Guid OrganizationId { get; set; }

    /// <summary>
    /// What this person may do <em>in this family</em>. Exactly one active membership per
    /// organization carries <see cref="UserRole.Admin"/>, and that person is the payer.
    /// </summary>
    public UserRole Role { get; set; } = UserRole.Member;

    /// <summary>When the membership began — onboarding, or the moment an Admin approved them.</summary>
    public DateTime JoinedDate { get; set; }

    /// <summary>
    /// False once the person has left or been removed. Kept rather than deleted so an audit
    /// question — was this person ever in this family — still has a row to answer from; the
    /// erasure cascade removes it for good.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public UserOrganization()
    {
        JoinedDate = DateTime.UtcNow;
    }
}
