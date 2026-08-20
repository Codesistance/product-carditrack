using CardiTrack.Application.DTOs.Common;

namespace CardiTrack.Application.DTOs.Responses;

public class MemberChatMessageResponse
{
    public required Guid SessionId { get; init; }
    public required string Reply { get; init; }
    public required IReadOnlyList<ChartSeries> Charts { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
}
