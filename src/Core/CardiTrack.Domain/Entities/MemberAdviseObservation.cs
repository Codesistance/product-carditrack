using CardiTrack.Domain.Common;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// One thing the Advise pass noticed in a member's readings, kept as a dated entry rather than
/// overwritten — the record a caregiver can take to an appointment and say what has changed since
/// the last one.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to <see cref="MemberAdvise"/>, deliberately not a change to it.
/// <see cref="MemberAdvise"/> is current guidance: one row per member per topic, overwritten every
/// pass, and the Dashboard's pulse badge is a bare "does a row exist" read against it with no model
/// call. Turning that row into a history would have cost the dashboard that read. So the current
/// row keeps its shape and this table carries the record beside it.
/// </para>
/// <para>
/// Append-only, and appended to only when the pass says something <em>different</em> from that
/// topic's last entry. A daily generator that keeps reaching the same conclusion writes nothing,
/// so the log reads as a history of what changed rather than a diary of the same sentence
/// restated — which is the difference between a page worth taking to a doctor and one nobody
/// finishes.
/// </para>
/// <para>
/// Derived CardiTrack prose, not caregiver free text and not a reading: every row here is copy
/// that already passed <c>AdviseRegisterGuards</c> on its way to a family's screen, so it names no
/// condition and proposes no treatment. It is still about a person's health, so it is bounded by
/// retention like everything else of that class — see <c>RetentionWorkerOptions</c>.
/// </para>
/// </remarks>
public class MemberAdviseObservation : BaseEntity
{
    public Guid CardiMemberId { get; set; }

    /// <summary>Which area of wellbeing this entry was about — see <see cref="AdviseTopic"/>.</summary>
    public AdviseTopic Topic { get; set; } = AdviseTopic.General;

    /// <summary>What was noticed in the readings, in the same everyday words the family saw.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>What was suggested at the time, kept so an entry reads as it did on the day.</summary>
    public string Suggestion { get; set; } = string.Empty;

    /// <summary>Which wellness reference the suggestion drew on, when one was named.</summary>
    public string? GuidelineCited { get; set; }

    /// <summary>
    /// When the pass that produced this entry ran. Its own column rather than
    /// <see cref="BaseEntity.CreatedDate"/>, which the base constructor sets at <c>new</c> time:
    /// the generator stamps every row in a pass with one instant, and a log ordered by when each
    /// object happened to be constructed is a log that can disagree with itself.
    /// </summary>
    public DateTime ObservedAtUtc { get; set; }
}
