namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// A wearer-side device invitation as the caregiver's app sees it.
/// </summary>
/// <remarks>
/// <see cref="Url"/> is populated exactly once, in the response to the call that created the
/// invite. Every later read — the poll the waiting screen runs, most of all — leaves it null: the
/// app already has the URL it needs, and a status endpoint that kept re-issuing a live credential
/// would turn every poll into another chance to leak one.
/// </remarks>
public class DeviceInviteResponse
{
    public Guid InviteId { get; set; }

    /// <summary>Wire name of the brand the wearer is being asked to authorize.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>How it was handed over: <c>link</c> or <c>qr</c>.</summary>
    public string Channel { get; set; } = string.Empty;

    /// <summary>
    /// <c>pending</c>, <c>opened</c>, <c>completed</c>, <c>declined</c>, <c>revoked</c> or
    /// <c>expired</c>. Expiry is reported here as a status even though it is stored as a timestamp,
    /// because it is what the waiting screen has to render and the client should not be re-deriving
    /// it from a clock that may not agree with ours.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// The link to hand the wearer — the QR code's payload, or the text the share sheet sends.
    /// Null on every read after creation; see the remarks on this class.
    /// </summary>
    public string? Url { get; set; }

    public DateTime ExpiresAt { get; set; }
    public DateTime? OpenedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }

    /// <summary>The connection the wearer's consent produced, once <c>completed</c>.</summary>
    public Guid? DeviceId { get; set; }

    /// <summary>The connection this invitation replaces once completed; null when it adds a device.</summary>
    public Guid? ReplacesDeviceId { get; set; }
}
