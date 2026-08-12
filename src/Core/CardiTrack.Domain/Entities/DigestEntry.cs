using CardiTrack.Domain.Enums;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// One generated summary: a plain-language account of how a member is doing, written by the
/// private medical model for one audience. Derived data — regenerable, never authoritative
/// (docs/llm_design.md).
/// <para>
/// Composite-keyed (CardiMemberId, LocalDate, Audience, GeneratedAtUtc) and month-partitioned on
/// <see cref="LocalDate"/>, the same shape as <see cref="MetricRollupHourly"/>: the natural key
/// carries the partition column, and retention is a partition drop.
/// </para>
/// <para>
/// <see cref="GeneratedAtUtc"/> is part of that key because a summary is now recomputed whenever
/// the member's data moves, not once a morning: every generation is kept, so the row set for a
/// day is that day's history rather than a single cell overwritten in place. Readers that want
/// "the summary" ask for the latest.
/// </para>
/// </summary>
public class DigestEntry
{
    public Guid CardiMemberId { get; set; }

    /// <summary>
    /// The member's local calendar day the summary describes — local, not UTC, because "today"
    /// in a summary means the reader's today. The partition key.
    /// </summary>
    public DateOnly LocalDate { get; set; }

    public DigestAudience Audience { get; set; }

    /// <summary>
    /// A few words naming what this particular summary is about ("A settled night", "Moving less
    /// than usual") — generated with the text, so it describes this generation rather than
    /// labelling the screen it lands on. Null on entries written before headlines existed.
    /// </summary>
    public string? Headline { get; set; }

    /// <summary>The generated summary text, as delivered to the reader.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Three short things the family could do to support this CardiMember today, generated with
    /// the summary so they follow from the same readings it describes rather than from a fixed
    /// list. Null when the model returned none, or returned some that did not survive validation
    /// — the apps show nothing rather than a partial set.
    /// </summary>
    /// <remarks>
    /// Support, never treatment: these are the register of "ask how they slept", not of dosage or
    /// diagnosis. Derived and regenerable like the rest of this entity, and never authoritative
    /// (docs/llm_design.md).
    /// </remarks>
    public List<string>? Suggestions { get; set; }

    /// <summary>
    /// When this generation ran. Part of the key: it is what distinguishes one recomputation of a
    /// day from the next, and ordering by it is how readers find the current summary.
    /// </summary>
    public DateTime GeneratedAtUtc { get; set; }
}
