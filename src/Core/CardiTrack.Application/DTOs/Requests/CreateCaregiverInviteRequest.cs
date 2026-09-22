namespace CardiTrack.Application.DTOs.Requests;

/// <summary>
/// Asks for a caregiver invitation
/// (POST /api/v1/cardimembers/{id}/caregiver-invites).
/// </summary>
/// <remarks>
/// Note what is not here: no email address, no phone number, no name. The admin delivers the link
/// themselves through their own phone's share sheet, so CardiTrack never learns who it went to and
/// never sends anything on their behalf — the same position
/// <see cref="CreateDeviceInviteRequest"/> takes for the wearer's invitation.
/// </remarks>
public class CreateCaregiverInviteRequest
{
    /// <summary>
    /// The role the invitee's family membership will carry: <c>member</c> or <c>admin</c>.
    /// Choosing admin hands the family — and its plan — to them, because a family has exactly one.
    /// </summary>
    public string Role { get; set; } = "member";

    /// <summary>Whether they will be able to see this member's health data. Defaults to yes.</summary>
    public bool CanViewHealthData { get; set; } = true;

    /// <summary>Whether they will be an alert recipient for this member. Defaults to yes.</summary>
    public bool ReceiveAlerts { get; set; } = true;
}
