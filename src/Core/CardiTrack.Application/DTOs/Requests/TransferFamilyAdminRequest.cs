namespace CardiTrack.Application.DTOs.Requests;

/// <summary>
/// Names the person a family is being handed to
/// (PUT /api/v1/families/{organizationId}/admin).
/// </summary>
public class TransferFamilyAdminRequest
{
    /// <summary>
    /// The member who becomes admin. They must already be in the family: handing it to somebody
    /// outside would be an invitation, which is a different act with a different consent step.
    /// </summary>
    public Guid UserId { get; set; }
}
