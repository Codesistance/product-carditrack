using CardiTrack.Application.DTOs.Common;

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

    /// <summary>The reply's supporting series, so a resumed or refreshed conversation draws the
    /// charts it drew when the answer arrived. Empty on caregiver turns and on replies that had
    /// nothing to chart.</summary>
    public IReadOnlyList<ChartSeries> Charts { get; init; } = [];
    public required DateTimeOffset CreatedAtUtc { get; init; }
}
