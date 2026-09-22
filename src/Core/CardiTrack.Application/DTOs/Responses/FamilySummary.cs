namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// One family as the person browsing their own families sees it.
/// </summary>
/// <param name="OrganizationId">The family.</param>
/// <param name="Name">Its name, as whoever started it chose.</param>
/// <param name="Role">The caller's role in it: <c>member</c> or <c>admin</c>.</param>
/// <param name="IsHomeFamily">
/// Whether this is the family the caller started and pays for, as opposed to one they joined.
/// </param>
/// <param name="MemberCount">How many people are in the family.</param>
/// <param name="WatchedMemberNames">
/// The CardiMembers in this family the caller can actually see, by name. Not every member of the
/// family — only what this caller was granted, because the list is what they are able to open.
/// </param>
public record FamilySummary(
    Guid OrganizationId,
    string Name,
    string Role,
    bool IsHomeFamily,
    int MemberCount,
    IReadOnlyList<string> WatchedMemberNames);
