namespace CardiTrack.Application.DTOs.Responses;

public class BaselineInsightResponse
{
    public required Guid CardiMemberId { get; init; }
    public required string Summary { get; init; }
    public required IReadOnlyList<string> KeyFindings { get; init; }

    /// <summary>
    /// True while the member has no baseline at all yet. The summary then describes what has been
    /// observed so far rather than how today compares to normal, so clients must not present it as a
    /// trend assessment — it matches the dashboard's learning state.
    /// </summary>
    public required bool IsLearning { get; init; }

    /// <summary>
    /// True when the summary is anchored to a provisional (sub-30-day) baseline: an early
    /// impression phrased tentatively, not an established trend. Matches the dashboard's
    /// provisional state so the two surfaces never disagree.
    /// </summary>
    public bool IsProvisional { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }
}
