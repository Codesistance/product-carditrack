using CardiTrack.Domain.Common;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// An admin's invitation to another adult to help watch one CardiMember.
/// </summary>
/// <remarks>
/// <para>
/// Modelled on <see cref="DeviceConnectionInvite"/>, which solved the same problem for the wearer:
/// a credential handed to somebody who is not yet in the system, addressed by the sender rather
/// than by us. The two differ in exactly one way that matters, and it is the reason this is a
/// separate type rather than a flag on that one — a wearer's invitation ends
/// <em>anonymously</em>, while this one must end in an authenticated account. So the token here
/// authorizes creating a membership and a grant, and never a read of health data: everything the
/// invitee eventually sees, they see because they signed in and hold a
/// <see cref="UserCardiMember"/> row, not because they hold this token.
/// </para>
/// <para>
/// <strong>What it holds is deliberately thin.</strong> No invitee name, no email, no phone
/// number: the admin addresses the message themselves through their own phone's share sheet, so we
/// never learn who they sent it to. The landing page reads the member's first name from
/// <see cref="CardiMemberId"/> at render time rather than copying it here, so a revoked invitation
/// stops being able to say anyone's name at all.
/// </para>
/// <para>
/// <strong>The grant travels on the row.</strong> <see cref="Role"/>,
/// <see cref="CanViewHealthData"/> and <see cref="ReceiveAlerts"/> are decided when the invitation
/// is written and applied when it is redeemed, so what somebody is being offered is fixed at the
/// moment the admin chose it and cannot drift afterwards. Carrying the role here is also what
/// keeps a live grant from ever needing a migration to acquire one.
/// </para>
/// <para>
/// <strong>The token is never stored.</strong> <see cref="TokenHash"/> is a SHA-256 of it, minted
/// and hashed by the same helper the wearer invitations use.
/// </para>
/// </remarks>
public class CaregiverInvite : BaseEntity
{
    /// <summary>The member this invitation grants access to, and whose first name the landing page shows.</summary>
    public Guid CardiMemberId { get; set; }

    /// <summary>
    /// The family the invitee joins. Derived from the member when the invitation is written, and
    /// stored rather than re-derived, so a redemption applies the family the admin was looking at
    /// even if the member has since been moved.
    /// </summary>
    public Guid OrganizationId { get; set; }

    /// <summary>
    /// The admin who issued it. They are the one whose authority is re-checked when somebody
    /// redeems — an invitation must not outlive the authority that issued it.
    /// </summary>
    public Guid CreatedByUserId { get; set; }

    /// <summary>What the invitee's membership of <see cref="OrganizationId"/> will say.</summary>
    public UserRole Role { get; set; } = UserRole.Member;

    /// <summary>Whether the grant this invitation creates can see the member's health data.</summary>
    public bool CanViewHealthData { get; set; } = true;

    /// <summary>Whether the grant this invitation creates is an alert recipient.</summary>
    public bool ReceiveAlerts { get; set; } = true;

    /// <summary>
    /// SHA-256 of the invitation token, lower-case hex. The token itself is returned once, at
    /// creation, and never again — not by the list, not in a log line, and not from here.
    /// </summary>
    public string TokenHash { get; set; } = string.Empty;

    public CaregiverInviteStatus Status { get; set; } = CaregiverInviteStatus.Pending;

    /// <summary>
    /// When it stops working. Past this instant the invitation is dead regardless of
    /// <see cref="Status"/> — see the remarks on <see cref="CaregiverInviteStatus"/>.
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>When somebody first opened the link, if anyone ever did.</summary>
    public DateTime? OpenedAt { get; set; }

    /// <summary>When it reached a terminal state — accepted, declined or revoked.</summary>
    public DateTime? ResolvedAt { get; set; }

    /// <summary>
    /// Who redeemed it, once somebody has. Null in every other state. This is what lets "the
    /// invitation Jane sent on Tuesday is why Tom can see Margaret" be answered later without
    /// inferring it from timestamps.
    /// </summary>
    public Guid? AcceptedByUserId { get; set; }
}
