namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// The longer view of one CardiMember: where their readings have been going over the weeks, as
/// written by the daily trend pass from figures computed in .NET against the pinned reference
/// ranges.
/// </summary>
/// <remarks>
/// A blank <see cref="Narrative"/> means there is nothing to say yet rather than nothing to say —
/// most often a member with under a month of readings, which is the learning state and not a
/// trajectory. Never a risk score, a probability or a prediction: see
/// <c>TrendInterpretationService</c> and the design's "what is never produced".
/// </remarks>
public class TrendInsightResponse
{
    public required Guid CardiMemberId { get; set; }

    /// <summary>What has been happening over the stretch, in a few sentences. Empty when none.</summary>
    public required string Narrative { get; set; }

    /// <summary>Up to three short lines, each naming one movement worth noticing.</summary>
    public required IReadOnlyList<string> KeyFindings { get; set; }

    /// <summary>The longest baseline window the narrative was measured against. Null when none.</summary>
    public int? BaselinePeriodDays { get; set; }

    public required DateTimeOffset GeneratedAt { get; set; }
}
