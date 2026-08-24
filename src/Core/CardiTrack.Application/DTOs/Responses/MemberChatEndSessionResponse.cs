namespace CardiTrack.Application.DTOs.Responses;

/// <summary>Result of ending the caregiver's current conversation about a member.</summary>
public class MemberChatEndSessionResponse
{
    /// <summary>The session that was just ended, or null when there was no active conversation
    /// to end — which is a fine outcome, not an error: the caregiver asked for a fresh start and
    /// has one either way.</summary>
    public Guid? EndedSessionId { get; init; }
}
