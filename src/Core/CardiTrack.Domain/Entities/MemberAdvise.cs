using CardiTrack.Domain.Common;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// One member's current wellness suggestion for one topic — the latest output of the batch Advise
/// pass, served read-only to caregivers. One row per member per <see cref="AdviseTopic"/>,
/// overwritten on each regeneration (no history: the suggestion is current guidance, not a
/// record).
/// </summary>
/// <remarks>
/// Persisted for the same reason as <see cref="MemberStatusLine"/>: the writer (the pipeline job)
/// and the reader (the API) are different processes, and this row is what lets a dashboard load
/// and the Dashboard hero card's pulse indicator stay a plain read with no model call on the
/// request path.
/// </remarks>
public class MemberAdvise : BaseEntity
{
    public Guid CardiMemberId { get; set; }

    /// <summary>Which area of wellbeing this suggestion addresses — see <see cref="AdviseTopic"/>.
    /// Part of the row's identity: the unique index is (member, topic).</summary>
    public AdviseTopic Topic { get; set; } = AdviseTopic.General;

    /// <summary>What in the readings prompted the suggestion.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>The suggestion itself, name already resolved — never a placeholder.</summary>
    public string Suggestion { get; set; } = string.Empty;

    /// <summary>
    /// Which wellness reference the suggestion drew on. Null when the model found nothing in the
    /// readings that fit any reference — see <c>AdviseGenerationService</c>'s handling of that
    /// reply, which withholds the row entirely rather than persisting a suggestion with no
    /// grounding.
    /// </summary>
    public string? GuidelineCited { get; set; }

    /// <summary>When the batch generated this suggestion. The API withholds a row older than its
    /// staleness ceiling rather than serving stale guidance as if it were current.</summary>
    public DateTime GeneratedAtUtc { get; set; }
}
