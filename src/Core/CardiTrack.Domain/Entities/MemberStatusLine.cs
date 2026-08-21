using CardiTrack.Domain.Common;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// The Dashboard hero card's live status line for one member — the latest headline and sentence
/// the digest/assess batch passes generated, served read-only to caregivers. One row per member,
/// overwritten on each regeneration (no history: the line is ambient copy, not a record — the
/// digest table is the durable narrative).
/// </summary>
/// <remarks>
/// Persisted rather than cached because the writer and the reader are different processes:
/// the pipeline jobs generate, the API serves, and Redis exists only in dev — in prod an
/// IDistributedCache write from a job would never reach the API's in-process cache. This row is
/// also what lets the request path stop calling the model entirely, which is what makes a
/// scale-to-zero GPU MedGemma viable (docs/technical/medgemma_serving_architecture.md, Option B).
/// </remarks>
public class MemberStatusLine : BaseEntity
{
    public Guid CardiMemberId { get; set; }

    /// <summary>Short card title, two to five words; null when the model's headline was dropped
    /// (the dashboard keeps its per-tier headline and still shows the sentence).</summary>
    public string? Headline { get; set; }

    /// <summary>The one-sentence status line, name already resolved — never a placeholder.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>When the batch generated this line. The API withholds a line older than its
    /// staleness ceiling rather than serving yesterday's reassurance as if it were current.</summary>
    public DateTime GeneratedAtUtc { get; set; }
}
