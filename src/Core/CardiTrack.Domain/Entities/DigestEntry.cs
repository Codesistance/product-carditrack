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
    /// One short, specific thing the family could do to support this CardiMember today, generated
    /// with the summary so it follows from the same readings the summary describes. Null when the
    /// model returned none, or returned one that did not survive validation — the apps show
    /// nothing rather than a mangled or unsafe line.
    /// </summary>
    /// <remarks>
    /// Support, never treatment: register is "ask how they slept," not dosage or diagnosis — it
    /// may reference an already-known routine fact like a scheduled medication, but never names or
    /// guesses at a condition. Derived and regenerable like the rest of this entity, and never
    /// authoritative (docs/llm_design.md).
    /// </remarks>
    public string? Suggestion { get; set; }

    /// <summary>
    /// The model's own read of how soon the family should act — see <see cref="DigestUrgency"/>
    /// for why this runs alongside, never in place of, the deterministic alert engine. Null when
    /// the model returned nothing parseable, or on entries written before this field existed.
    /// </summary>
    public DigestUrgency? Urgency { get; set; }

    /// <summary>
    /// When this generation ran. Part of the key: it is what distinguishes one recomputation of a
    /// day from the next, and ordering by it is how readers find the current summary.
    /// </summary>
    public DateTime GeneratedAtUtc { get; set; }

    /// <summary>
    /// Which version of this service's briefs wrote the row — see
    /// <c>DigestGenerationService.CurrentPromptVersion</c>. Rows from before the column exist at 0.
    /// </summary>
    /// <remarks>
    /// The same mechanism <see cref="MemberAdvise.PromptVersion"/> carries, and it exists here for
    /// a failure that mechanism would have prevented: the family summary's gates all turn on the
    /// readings moving, so copy written under a brief since corrected — a pronoun the model chose
    /// for itself, before it was asked for a token the code resolves — stayed on the card of any
    /// member whose readings had gone quiet, with nothing in the pass ever looking at it again.
    /// A row from an older version is stale whatever the data did.
    /// <para>
    /// Stamped on every audience this service writes, family and journals alike, because it
    /// records which briefs produced the text rather than which gate reads it back. Only the
    /// family path acts on it: a journal entry is an account of a finished day, written once, and
    /// a better brief is not a reason to rewrite a day that has already been read.
    /// </para>
    /// </remarks>
    public int PromptVersion { get; set; }
}
