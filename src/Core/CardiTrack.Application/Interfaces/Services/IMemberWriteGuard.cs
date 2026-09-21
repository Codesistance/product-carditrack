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
/// erasure waits, then sweeps the row that was written as part of its ordinary cascade.
/// </para>
/// <para>
/// <strong>Deadlock-freedom is conditional, not free.</strong> It holds only while both sides lock
/// <c>CardiMembers</c> before touching anything the cascade will also touch. A transaction that
/// updates a member-scoped row first and reaches this guard second holds what erasure is about to
/// want while waiting for what erasure already has, and PostgreSQL resolves that by killing one of
/// them. Two callers were in exactly that position — the journal rung's chat turn and
/// <c>ReplaceBookAsync</c> — and both now take <see cref="HoldMembersAsync"/> as the first
/// statement of their transaction. A new caller that opens a transaction, writes member-scoped
/// rows, and only then calls <see cref="WriteIfMembersLiveAsync"/> reintroduces the deadlock.
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

    /// <summary>
    /// Takes the member lock inside a transaction the caller has already opened, without writing
    /// anything. For a transaction that touches other member-scoped rows <em>before</em> it
    /// reaches its guarded write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because "both sides lock the member row first" is what makes the guard
    /// deadlock-free, and a caller can break it without noticing. The journal rung opens the chat
    /// turn's transaction and updates <c>MemberChatSessions</c> before the turns are saved;
    /// <see cref="IDigestRepository.ReplaceBookAsync"/> deletes the old book before inserting the
    /// new one. Both would then hold a member-scoped row and wait on <c>CardiMembers</c>, while an
    /// erasure holds <c>CardiMembers</c> and waits on that same row — a genuine PostgreSQL
    /// deadlock, and one of the two transactions is killed rather than made to wait.
    /// </para>
    /// <para>
    /// Called as the first statement after the transaction opens, it restores the ordering: this
    /// transaction holds <c>CardiMembers</c> before it touches anything the cascade will reach, so
    /// erasure waits for it exactly as it does for any other writer.
    /// </para>
    /// <para>
    /// The lock lives as long as the transaction, so there must be one: without it the lock would
    /// be taken and released by its own implicit transaction and guarantee nothing, which is worse
    /// than not calling this at all because it reads as protection. That case throws.
    /// </para>
    /// </remarks>
    /// <returns>
    /// False when the member is already erased — the caller must abandon the transaction rather
    /// than continue, since everything it was about to write describes someone who is gone.
    /// </returns>
    Task<bool> HoldMembersAsync(IReadOnlyCollection<Guid> cardiMemberIds, CancellationToken ct = default);

    /// <summary>The single-member case of <see cref="HoldMembersAsync"/>.</summary>
    Task<bool> HoldMemberAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        HoldMembersAsync([cardiMemberId], ct);
}
