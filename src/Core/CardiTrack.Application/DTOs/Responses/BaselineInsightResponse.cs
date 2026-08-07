namespace CardiTrack.Application.DTOs.Responses;

public class BaselineInsightResponse
{
    public required Guid CardiMemberId { get; init; }
    public required string Summary { get; init; }
    public required IReadOnlyList<string> KeyFindings { get; init; }

    /// <summary>
    /// True while the member has no 30-day baseline yet. The summary then describes what has been
    /// observed so far rather than how today compares to normal, so clients must not present it as a
    /// trend assessment — it matches the dashboard's learning state.
    /// </summary>
    public required bool IsLearning { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }
}
