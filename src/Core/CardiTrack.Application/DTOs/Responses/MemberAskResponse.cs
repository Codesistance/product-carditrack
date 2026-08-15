namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// One answer to a caregiver's question about a CardiMember. Not stored: the exchange lives
/// on the client for this visit. The question is echoed so the client can render the pair
/// without keeping a parallel copy of what it sent.
/// </summary>
public class MemberAskResponse
{
    public required Guid CardiMemberId { get; init; }

    /// <summary>The question as sent to the model (flattened, trimmed).</summary>
    public required string Question { get; init; }

    /// <summary>The model's reply, with the member's first name substituted for the placeholder.</summary>
    public required string Answer { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }
}
