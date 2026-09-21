namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// Makes a write conditional on its CardiMember still existing, atomically against a concurrent
/// <see cref="IMemberErasureService"/> run.
/// </summary>
/// <remarks>
/// <para>
/// The problem this exists for: every AI generator reads the member, calls a model for seconds to
/// minutes, then writes a row naming that <c>CardiMemberId</c>. The schema has no foreign key to
/// stop it — "Guid references only" is the documented design principle — so an erasure landing
/// between the read and the write completes, and the generation then re-creates health data for a
/// member who has been erased. In a health product that is an Art. 17 failure, and no amount of
/// checking before the write fixes it: a check is not the write.
/// </para>
/// <para>
/// <strong>Why a lock and not an existence check.</strong> The obvious fix — an
/// <c>EXISTS (SELECT 1 FROM "CardiMembers" …)</c> predicate on the insert — reads as atomic and is
/// not. Under READ COMMITTED a plain subquery takes no lock and cannot see an erasure
/// transaction's uncommitted deletes, so it answers "yes, still there", the row inserts, and the
/// erasure commits on top of it. Measured against <c>postgres:17-alpine</c>: erasure mid-flight,
/// guarded insert, one orphaned row and zero members. It narrows the window from minutes to
/// milliseconds and leaves the hole open — which for a compliance register is the worst of both,
/// because it reads as closed.
/// </para>
/// <para>
/// What closes it is the row lock a real foreign key would have taken. This guard holds
/// <c>FOR KEY SHARE</c> on each member row for the lifetime of the write's transaction, and
/// <see cref="IMemberErasureService"/> takes <c>FOR UPDATE</c> on the same row as the first
/// statement of its own. The two conflict, so one of the two orderings always happens and both are
/// safe: erasure first and the guard finds no row to lock (nothing is written); the guard first and
/// erasure waits, then sweeps the row that was written as part of its ordinary cascade. Both sides
/// lock the same row before touching anything else, so there is no deadlock.
/// </para>
/// <para>
/// <strong>The transaction must not span a model call.</strong> The lock is held until the write
/// commits, so a caller that opens the guard before inference would block erasure for the length of
/// a MedGemma call — up to the 900s client budget. Call this <em>after</em> the model has answered,
/// around the persistence and nothing else. The one deliberate exception is
/// <c>ReportGenerationService</c>, which holds it across the export upload as well, because the
/// object must not be written for a member the row can no longer name.
/// </para>
/// </remarks>
public interface IMemberWriteGuard
{
    /// <summary>
    /// Runs <paramref name="write"/> only if every named member still exists, and only for as long
    /// as they are guaranteed to keep existing.
    /// </summary>
    /// <param name="cardiMemberIds">
    /// Every member the write would describe. All of them are locked — a report naming four members
    /// must not be written when one has been erased, because erasure deletes that whole row.
    /// Duplicates and order do not matter; the guard locks in a fixed order of its own.
    /// </param>
    /// <param name="write">
    /// The persistence, and only the persistence. Runs inside the guard's transaction — a
    /// <c>SaveChangesAsync</c>, a raw statement, whatever the caller normally does — and must not
    /// call a model.
    /// </param>
    /// <returns>
    /// True when the write ran and committed. False when at least one member was already erased, in
    /// which case nothing was written and nothing was staged: the caller's unit of work is rolled
    /// back to where it was. Callers return their ordinary "produced nothing" result on false — a
    /// refused write is not an error, it is a generation that arrived too late to matter.
    /// </returns>
    Task<bool> WriteIfMembersLiveAsync(
        IReadOnlyCollection<Guid> cardiMemberIds,
        Func<CancellationToken, Task> write,
        CancellationToken ct = default);

    /// <summary>The single-member case, which is nearly all of them.</summary>
    Task<bool> WriteIfMemberLivesAsync(
        Guid cardiMemberId, Func<CancellationToken, Task> write, CancellationToken ct = default) =>
        WriteIfMembersLiveAsync([cardiMemberId], write, ct);
}
