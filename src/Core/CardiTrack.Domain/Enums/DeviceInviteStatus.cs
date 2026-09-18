namespace CardiTrack.Domain.Enums;

/// <summary>
/// Where a <see cref="Entities.DeviceConnectionInvite"/> has got to. Persisted by name.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Pending"/> and <see cref="Opened"/> are the live states — an invite in either can
/// still be completed, and at most one of them may exist per member and brand. Everything else is
/// terminal.
/// </para>
/// <para>
/// There is no Expired state, deliberately. Expiry is a fact about the clock, not a transition
/// somebody performs: an invite whose <see cref="Entities.DeviceConnectionInvite.ExpiresAt"/> has
/// passed is expired whether or not any job has been round to say so. Storing it as a status would
/// mean a row is only really expired once something has swept it, and every read would have to
/// check the clock anyway to be correct in the window before the sweep.
/// </para>
/// </remarks>
public enum DeviceInviteStatus
{
    /// <summary>Created and handed over; nobody has opened the link yet.</summary>
    Pending = 1,

    /// <summary>The wearer has opened the link. Still completable — they may not have finished.</summary>
    Opened = 2,

    /// <summary>The wearer granted access and a connection was stored. Terminal.</summary>
    Completed = 3,

    /// <summary>The wearer said it was not them, or declined on our page. Terminal.</summary>
    Declined = 4,

    /// <summary>
    /// Withdrawn from our side — the caregiver cancelled, or created a replacement invite that
    /// superseded this one. Terminal.
    /// </summary>
    Revoked = 5,
}
