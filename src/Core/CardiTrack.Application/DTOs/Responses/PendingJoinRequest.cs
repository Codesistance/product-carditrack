namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// Somebody waiting on this family's admin, as the approval screen sees them.
/// </summary>
/// <remarks>
/// Name and email, because an admin approving a stranger needs to know they are approving the
/// right person and a name alone does not settle it. Nothing else about them: not their other
/// families, not what else they watch, not when they last signed in.
/// </remarks>
/// <param name="RequestId">The request to approve or decline.</param>
/// <param name="UserId">Who is asking.</param>
/// <param name="Name">Their name.</param>
/// <param name="Email">Their email.</param>
/// <param name="RequestedAt">When they asked.</param>
/// <param name="ExpiresAt">When the request stops being answerable.</param>
public record PendingJoinRequest(
    Guid RequestId,
    Guid UserId,
    string Name,
    string Email,
    DateTime RequestedAt,
    DateTime ExpiresAt);
