namespace CardiTrack.Domain.Enums;

/// <summary>
/// Where a <see cref="Entities.CaregiverInvite"/> has got to. Persisted by name.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Pending"/> and <see cref="Opened"/> are the live states — an invitation in either can
/// still be accepted. Everything else is terminal.
/// </para>
/// <para>
/// There is no Expired state, for the same reason
/// <see cref="DeviceInviteStatus"/> has none: expiry is a fact about the clock rather than a
/// transition somebody performs, and storing it would mean an invitation is only really expired
/// once a sweep has been round to say so.
/// </para>
/// </remarks>
public enum CaregiverInviteStatus
{
    /// <summary>Created and handed over; nobody has opened the link yet.</summary>
    Pending = 1,

    /// <summary>Somebody opened the link. Still acceptable — they may not have finished signing in.</summary>
    Opened = 2,

    /// <summary>Redeemed: the invitee has an account, a membership and their grants. Terminal.</summary>
    Accepted = 3,

    /// <summary>The invitee said no on the landing page. Terminal.</summary>
    Declined = 4,

    /// <summary>Withdrawn from our side — an admin cancelled it. Terminal.</summary>
    Revoked = 5,
}
