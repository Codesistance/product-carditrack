namespace CardiTrack.Application.DTOs.Responses;

/// <summary>The caregiver's completed conversations about one member — ended or lapsed, never
/// the one the chat window is still having — newest started first.</summary>
public class MemberChatSessionListResponse
{
    public required IReadOnlyList<MemberChatSessionSummaryResponse> Sessions { get; init; }
}

public class MemberChatSessionSummaryResponse
{
    public required Guid SessionId { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset LastTurnAtUtc { get; init; }

    /// <summary>What this conversation was about, as a short generated label — or null while the
    /// theming job hasn't visited this session yet, in which case the client shows
    /// <see cref="FirstQuestion"/> instead.</summary>
    public string? Theme { get; init; }

    /// <summary>The caregiver's opening question, decrypted — the row's fallback label until a
    /// theme exists.</summary>
    public required string FirstQuestion { get; init; }

    /// <summary>Caregiver questions only, not total turns — "3 questions" is the size a
    /// caregiver understands a conversation by.</summary>
    public required int QuestionCount { get; init; }
}
