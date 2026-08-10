using CardiTrack.Domain.Enums;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// One generated daily digest: a plain-language summary of a member's previous day, written by
/// the private medical model for one audience. Derived data — regenerable, never authoritative
/// (docs/llm_design.md).
/// <para>
/// Composite-keyed (CardiMemberId, LocalDate, Audience) and month-partitioned on
/// <see cref="LocalDate"/>, the same shape as <see cref="MetricRollupHourly"/>: the natural key
/// is also the idempotency key (one digest per member per local day per audience), and
/// retention is a partition drop.
/// </para>
/// </summary>
public class DigestEntry
{
    public Guid CardiMemberId { get; set; }

    /// <summary>
    /// The member's local calendar day the digest describes — local, not UTC, because "yesterday"
    /// in a digest means the reader's yesterday. The partition key.
    /// </summary>
    public DateOnly LocalDate { get; set; }

    public DigestAudience Audience { get; set; }

    /// <summary>The generated digest text, as delivered to the reader.</summary>
    public string Text { get; set; } = string.Empty;

    public DateTime GeneratedAtUtc { get; set; }
}
