namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// One person in a family, as the others in it see them.
/// </summary>
/// <remarks>
/// Name and email only — no phone number, no timezone, nothing about what they have looked at.
/// This is the roster, and a roster that grew contact details would be a directory of a health
/// service's users assembled from a screen anyone in the family can open.
/// </remarks>
/// <param name="UserId">The person.</param>
/// <param name="Name">Their name.</param>
/// <param name="Email">Their email, so an admin can tell two people with the same name apart.</param>
/// <param name="Role">Their role in this family: <c>member</c> or <c>admin</c>.</param>
/// <param name="IsYou">Whether this row is the caller.</param>
/// <param name="JoinedDate">When they joined.</param>
public record FamilyMemberSummary(
    Guid UserId,
    string Name,
    string Email,
    string Role,
    bool IsYou,
    DateTime JoinedDate);
