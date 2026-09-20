using CardiTrack.Domain.Common;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// One member's current interpretation of their own readings, at one <see cref="InsightScope"/> —
/// the latest output of a batch pass, served read-only to caregivers.
/// </summary>
/// <remarks>
/// <para>
/// Persisted for the same reason as <see cref="MemberStatusLine"/> and <see cref="MemberAdvise"/>:
/// the writer (a pipeline job) and the reader (the API) are different processes. Until this
/// existed, the alert and baseline insights were generated synchronously on the request path — a
/// caregiver tapping an alert paid a MedGemma cold start, or a 503 when the shared service was
/// catching up, for text the pipeline could have written in a pass it was already running.
/// </para>
/// <para>
/// The text here is model-written and about a named person, so it is persisted AI content in the
/// sense <c>docs/compliance/dpia.md</c> §6.3 means: it carries a retention period of its own
/// (<c>InsightRetention.MaxAge</c>), enforced by the Worker's sweep rather than by a partition
/// drop, because this table is ordinary EF-tracked rather than partitioned.
/// </para>
/// </remarks>
public class MemberInsight : BaseEntity
{
    public Guid CardiMemberId { get; set; }

    /// <summary>Which interpretation this is. Part of the row's identity.</summary>
    public InsightScope Scope { get; set; } = InsightScope.Baseline;

    /// <summary>
    /// The alert this explains, for <see cref="InsightScope.Alert"/> rows; null for the two
    /// member-scoped insights. Null rather than <see cref="Guid.Empty"/> so the partial unique
    /// index can tell "one row per alert" from "one row per member per scope" — Postgres counts
    /// nulls as distinct, which is why those are two filtered indexes rather than one composite.
    /// </summary>
    public Guid? AlertId { get; set; }

    /// <summary>
    /// The interpretation itself: the alert's explanation, the baseline summary, or the trend
    /// narrative. Name already resolved — never a placeholder.
    /// </summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// One thing the caregiver can do now. Null where the scope has no action to offer — the
    /// baseline and trend reads describe a picture rather than answering a specific event.
    /// </summary>
    public string? RecommendedAction { get; set; }

    /// <summary>
    /// The supporting points behind <see cref="Summary"/>, one per line. Stored as newline-joined
    /// text rather than JSON: it is an ordered list of sentences with no structure inside it, and
    /// a JSON column would invite one.
    /// </summary>
    public string? KeyFindings { get; set; }

    /// <summary>
    /// Whether the member had no baseline at all when this was written — the state the dashboard
    /// calls "getting to know you". The API reports it so the two surfaces never disagree.
    /// </summary>
    public bool IsLearning { get; set; }

    /// <summary>
    /// Whether the only baseline available was a short (7- or 14-day) window. Provisional baselines
    /// colour dashboards and soften phrasing; they never feed alert thresholds
    /// (docs/llm_design.md).
    /// </summary>
    public bool IsProvisional { get; set; }

    /// <summary>The window the baseline behind this covered, in days. Null while learning.</summary>
    public int? BaselinePeriodDays { get; set; }

    /// <summary>When the batch wrote this. The API withholds a row past its staleness ceiling
    /// rather than serving an old reading of the data as if it were current.</summary>
    public DateTime GeneratedAtUtc { get; set; }

    /// <summary>
    /// Which version of the generating brief wrote this row. A row from an older version is due for
    /// regeneration whatever its age, so a prompt change reaches every member within one pass
    /// instead of hiding behind the staleness ceiling. Rows from before the column exist at 0.
    /// </summary>
    public int PromptVersion { get; set; }
}
