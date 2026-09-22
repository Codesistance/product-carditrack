namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// A caregiver invitation as the admin's app sees it.
/// </summary>
/// <remarks>
/// <see cref="Url"/> is populated exactly once, in the response to the call that created the
/// invitation. Every later read leaves it null: the app already has the URL it needs, and a list
/// endpoint that kept re-issuing a live credential would turn every refresh into another chance to
/// leak one.
/// </remarks>
public class CaregiverInviteResponse
{
    public Guid InviteId { get; set; }

    /// <summary>The member the invitation grants access to.</summary>
    public Guid CardiMemberId { get; set; }

    /// <summary>The role the membership will carry: <c>member</c> or <c>admin</c>.</summary>
    public string Role { get; set; } = string.Empty;

    public bool CanViewHealthData { get; set; }

    public bool ReceiveAlerts { get; set; }

    /// <summary>
    /// <c>pending</c>, <c>opened</c>, <c>accepted</c>, <c>declined</c>, <c>revoked</c> or
    /// <c>expired</c>. Expiry is reported as a status even though it is stored as a timestamp,
    /// because it is what the list has to render and the client should not be re-deriving it from
    /// a clock that may not agree with ours.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// The link to hand the invitee. Null on every read after creation; see the remarks on this
    /// class.
    /// </summary>
    public string? Url { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime? OpenedAt { get; set; }

    public DateTime? ResolvedAt { get; set; }

    /// <summary>Who redeemed it, once <c>accepted</c>.</summary>
    public Guid? AcceptedByUserId { get; set; }
}
