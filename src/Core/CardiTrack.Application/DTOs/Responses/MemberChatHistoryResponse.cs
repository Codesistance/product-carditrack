namespace CardiTrack.Application.DTOs.Responses;

public class MemberChatHistoryResponse
{
    public required Guid SessionId { get; init; }
    public required IReadOnlyList<MemberChatTurnResponse> Turns { get; init; }
}

public class MemberChatTurnResponse
{
    public required string Role { get; init; }
    public required string Content { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
}
