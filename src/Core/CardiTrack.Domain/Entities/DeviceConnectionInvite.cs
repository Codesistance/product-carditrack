using CardiTrack.Domain.Common;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// One caregiver's invitation to a wearer to authorize their own wearable, opened on whatever
/// device the wearer is holding.
/// </summary>
/// <remarks>
/// <para>
/// Until this existed, connecting a device meant the wearer had to be standing next to the
/// caregiver with the caregiver's phone in their hand: the OAuth round trip ran inside the app, so
/// the consent screen could only appear there. That is fine for a son setting up his mother's watch
/// at her kitchen table and impossible for a daughter three hundred miles away. This row is the
/// handover — the caregiver starts the connection, the wearer finishes it somewhere else.
/// </para>
/// <para>
/// <strong>What it holds is deliberately thin.</strong> Two ids that already sit together in
/// <c>UserCardiMembers</c>, a brand, a hash, and some timestamps. No name, no email, no phone
/// number: the caregiver addresses the message themselves through their own phone's share sheet, so
/// we never learn who they sent it to. No health data of any kind. The wearer-facing page reads the
/// member's first name from <see cref="CardiMemberId"/> at render time rather than copying it here,
/// so a revoked invite stops being able to say anyone's name at all.
/// </para>
/// <para>
/// <strong>The token is never stored.</strong> <see cref="TokenHash"/> is a SHA-256 of it. The
/// token is 256 bits of randomness and is the entire authorization for the anonymous wearer
/// endpoints, so a database that leaked would otherwise hand over live invitations. Hashing costs
/// nothing here because lookup is by exact hash — there is no scan to make constant-time, and no
/// password-style stretching is warranted for a value with this much entropy that lives for hours.
/// </para>
/// <para>
/// <strong>A row, not a cache entry.</strong> The PKCE state this flow eventually mints does live
/// in the distributed cache, as it always has. The invite cannot: it must survive a Redis restart
/// (the wearer may open it tomorrow), be revocable by name, be listed back to the caregiver who is
/// watching for it to complete, and be answerable for afterwards — which of these went unopened is
/// a question the audit trail should be able to answer.
/// </para>
/// </remarks>
public class DeviceConnectionInvite : BaseEntity
{
    /// <summary>The wearer this invite is about. Also whose first name the wearer-facing page shows.</summary>
    public Guid CardiMemberId { get; set; }

    /// <summary>
    /// The caregiver who created it. They are the one whose access is re-checked when the wearer
    /// finishes — an invite must not outlive the authority that issued it — and the one the audit
    /// entries for the wearer's anonymous visits are filed against, since the wearer has no account
    /// to file them against.
    /// </summary>
    public Guid CreatedByUserId { get; set; }

    /// <summary>
    /// The hardware brand the wearer is being asked to authorize. Fixed at creation: the anonymous
    /// page must not let its visitor choose what they are consenting to, and a connection completed
    /// through this invite can only ever be for this brand.
    /// </summary>
    public DeviceType DeviceType { get; set; }

    /// <summary>How the caregiver handed it over. See <see cref="DeviceInviteChannel"/>.</summary>
    public DeviceInviteChannel Channel { get; set; }

    /// <summary>
    /// SHA-256 of the invite token, lower-case hex. The token itself is returned once, at creation,
    /// and never again — not by the status endpoint, not in a log line, and not from here.
    /// </summary>
    public string TokenHash { get; set; } = string.Empty;

    public DeviceInviteStatus Status { get; set; } = DeviceInviteStatus.Pending;

    /// <summary>
    /// When it stops working. Past this instant the invite is dead regardless of
    /// <see cref="Status"/> — see the remarks on <see cref="DeviceInviteStatus"/> for why expiry is
    /// a timestamp rather than a state.
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>When the wearer first opened the link, if they ever did.</summary>
    public DateTime? OpenedAt { get; set; }

    /// <summary>When it reached a terminal state — completed, declined or revoked.</summary>
    public DateTime? ResolvedAt { get; set; }

    /// <summary>
    /// The connection the wearer's consent produced, once it has. Null in every other state. This
    /// is what lets "the invite you sent on Tuesday is why this device is connected" be answered
    /// later without inferring it from timestamps.
    /// </summary>
    public Guid? DeviceConnectionId { get; set; }
}
