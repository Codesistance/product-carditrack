namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// The short lines the app cycles in the pending reply bubble while a member-chat send is in
/// flight — see <c>IMemberChatService.GetWaitingSentencesAsync</c>. Always non-empty: the service
/// substitutes canned lines when generation fails, so the client never has to invent copy.
/// </summary>
public class MemberChatWaitingResponse
{
    public required IReadOnlyList<string> Sentences { get; init; }
}
