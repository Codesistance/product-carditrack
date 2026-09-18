namespace CardiTrack.Domain.Enums;

/// <summary>
/// How a <see cref="Entities.DeviceConnectionInvite"/> was handed to the wearer. Persisted by name.
/// </summary>
/// <remarks>
/// This records what the caregiver chose, which is why it is stored rather than derived: the two
/// channels carry different lifetimes (a code on a screen is live for minutes, a message sits in an
/// inbox for a day), and after the fact the only way to explain why one invite expired sooner than
/// another is to know which it was.
/// </remarks>
public enum DeviceInviteChannel
{
    /// <summary>
    /// The caregiver sent the link — a message, an email, whatever their phone's share sheet
    /// offered. Longer-lived, because it has to survive sitting unread.
    /// </summary>
    Link = 1,

    /// <summary>
    /// A QR code shown on the caregiver's screen for the wearer to scan there and then. Short-lived:
    /// both people are in the room, so nothing is waiting on a delivery.
    /// </summary>
    QrCode = 2,
}
