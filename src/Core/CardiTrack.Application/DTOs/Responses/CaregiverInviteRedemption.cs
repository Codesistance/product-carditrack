namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// What redeeming a caregiver invitation produced.
/// </summary>
/// <param name="CardiMemberId">The member the redeemer can now see.</param>
/// <param name="OrganizationId">The family they are now a member of.</param>
/// <param name="Role">Their role in that family: <c>member</c> or <c>admin</c>.</param>
/// <param name="CanViewHealthData">Whether the grant includes the member's health data.</param>
/// <param name="ReceiveAlerts">Whether the grant makes them an alert recipient.</param>
/// <param name="AlreadyHadAccess">
/// True when the redeemer already held a grant on this member. The invitation still resolves — it
/// has been used and should stop working — but nothing about their existing access was widened.
/// </param>
public record CaregiverInviteRedemption(
    Guid CardiMemberId,
    Guid OrganizationId,
    string Role,
    bool CanViewHealthData,
    bool ReceiveAlerts,
    bool AlreadyHadAccess);
