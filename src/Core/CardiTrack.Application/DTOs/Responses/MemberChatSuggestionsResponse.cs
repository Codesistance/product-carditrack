namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// Ready-to-send question chips for the chat's empty state — the vocabulary of what the
/// assistant can answer, derived deterministically from the member's data state (no model call).
/// </summary>
public class MemberChatSuggestionsResponse
{
    public required IReadOnlyList<string> Suggestions { get; init; }
}
