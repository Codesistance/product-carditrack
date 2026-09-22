namespace CardiTrack.Application.DTOs.Requests;

/// <summary>
/// The admin's decision about somebody asking to join
/// (POST /api/v1/families/{organizationId}/join-requests/{id}/approve).
/// </summary>
/// <remarks>
/// Approval and the grant are one act. Letting somebody into a family and then deciding what they
/// can see would leave a person inside with no access and an admin with a second job to remember —
/// so what they get is chosen here, per member, in the same request that admits them.
/// </remarks>
public class ApproveJoinRequest
{
    /// <summary>
    /// The CardiMembers this person will be able to see. May be empty: an admin can admit somebody
    /// to the family without giving them anybody to watch yet.
    /// </summary>
    public IReadOnlyList<Guid> CardiMemberIds { get; set; } = [];

    /// <summary>
    /// Their role in the family: <c>member</c> or <c>admin</c>. Choosing admin hands them the
    /// family and its plan — a family has exactly one admin, so the approver becomes a member in
    /// the same act.
    /// </summary>
    public string Role { get; set; } = "member";

    /// <summary>Whether the grants make them an alert recipient for those members.</summary>
    public bool ReceiveAlerts { get; set; } = true;
}
