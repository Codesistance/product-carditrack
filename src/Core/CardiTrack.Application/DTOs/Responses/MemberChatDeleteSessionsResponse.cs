namespace CardiTrack.Application.DTOs.Responses;

/// <summary>Result of permanently deleting conversations from a caregiver's chat history.</summary>
public class MemberChatDeleteSessionsResponse
{
    /// <summary>How many conversations were actually deleted. Can be fewer than were asked for:
    /// ids that did not exist, or were not this caregiver's about this member, are skipped.</summary>
    public int DeletedCount { get; init; }
}
