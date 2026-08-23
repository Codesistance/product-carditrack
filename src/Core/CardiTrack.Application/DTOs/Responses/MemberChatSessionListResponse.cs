namespace CardiTrack.Application.DTOs.Responses;

/// <summary>The caregiver's past conversations about one member, newest activity first — what
/// the chat sheet's history list renders.</summary>
public class MemberChatSessionListResponse
{
    public required IReadOnlyList<MemberChatSessionSummaryResponse> Sessions { get; init; }
}

public class MemberChatSessionSummaryResponse
{
    public required Guid SessionId { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset LastTurnAtUtc { get; init; }

    /// <summary>The caregiver's opening question, decrypted — the line a history list is scanned
    /// by, since "what did I ask?" is how a past conversation is recognised.</summary>
    public required string FirstQuestion { get; init; }

    /// <summary>Caregiver questions only, not total turns — "3 questions" is the size a
    /// caregiver understands a conversation by.</summary>
    public required int QuestionCount { get; init; }
}
