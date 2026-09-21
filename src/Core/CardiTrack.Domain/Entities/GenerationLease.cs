using CardiTrack.Domain.Common;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// One member's claim on one <see cref="GenerationWork"/>, held across the model call that writes
/// it. What stops two overlapping pipeline executions both paying to generate the same period.
/// </summary>
/// <remarks>
/// <para>
/// The race is real rather than theoretical: the digest job is scheduled every thirty minutes and
/// its Cloud Run timeout is an hour, so a slow pass is still running when the next execution
/// starts. Both then read the same member, both see no book for the period, and both call the
/// model. The unique indexes downstream keep the <em>data</em> right — one execution's insert is
/// rejected, or the two write the same row with content computed from the same readings — so what
/// was being lost was inference, not correctness. On a service whose measured cost profile says
/// cadence is the only lever, that is the thing worth not losing.
/// </para>
/// <para>
/// <b>One row per member per work, not per period.</b> The claimed period lives in
/// <see cref="PeriodEnd"/> rather than in the key, which bounds the table at members × works and
/// means no retention sweep has to exist for it: next week's claim overwrites last week's the
/// moment the old lease has expired. A per-period key would have been one row per member per day
/// for the Daybook alone, and a sweep to match.
/// </para>
/// <para>
/// <b>A lease, not a lock.</b> It carries an expiry so that an execution killed mid-generation —
/// a deploy, an OOM, a Cloud Run timeout — costs the member one period's delay rather than
/// blocking that period for good. It is deliberately not the Worker's <c>AdvisoryLock</c>, whose
/// own remarks say it is "restraint, not correctness" and that work needing exclusivity "wants a
/// claim it can lose safely, not this"; a session-scoped lock would also hold a database
/// connection open for the whole of a model call.
/// </para>
/// <para>
/// Holds no health data: a member id, which writer, which period, and two timestamps.
/// </para>
/// </remarks>
public class GenerationLease : BaseEntity
{
    public Guid CardiMemberId { get; set; }

    /// <summary>Which writer holds the claim. Part of the row's identity: the unique index is
    /// (member, work), and it is this upsert's conflict target.</summary>
    public GenerationWork Work { get; set; }

    /// <summary>
    /// The last day of the period being generated — the same date its book or narrative is dated
    /// by, so a held row says which period is in flight rather than only that something is.
    /// </summary>
    public DateOnly PeriodEnd { get; set; }

    /// <summary>
    /// When the claim lapses. A later execution may take the lease over at or after this instant,
    /// whether or not the holder ever released it.
    /// </summary>
    public DateTime HeldUntilUtc { get; set; }

    /// <summary>When the current holder took it.</summary>
    public DateTime ClaimedAtUtc { get; set; }
}
