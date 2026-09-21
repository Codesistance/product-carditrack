using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Interfaces.Repositories;

/// <summary>
/// The claim a once-per-period writer takes before it pays for a model call, so two overlapping
/// pipeline executions cannot both generate the same period.
/// </summary>
public interface IGenerationLeaseRepository
{
    /// <summary>
    /// Takes the claim for one member's <paramref name="work"/> over
    /// <paramref name="periodEnd"/>, or returns false when another execution already holds a
    /// live one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One statement, and the atomicity is Postgres': an <c>INSERT ... ON CONFLICT DO UPDATE</c>
    /// whose <c>WHERE</c> admits the update only when the existing lease has lapsed. Two
    /// executions racing it cannot both come back with a row — the loser matches no row and is
    /// told so by an affected-row count of zero. A read-then-write could not promise that, which
    /// is the whole reason this is not a query and a save.
    /// </para>
    /// <para>
    /// Callers treat a false as "someone else has this period" and move to the next member. They
    /// do not wait: the other execution is doing the identical work, so queueing behind it would
    /// buy a duplicate rather than avoid one.
    /// </para>
    /// </remarks>
    /// <param name="heldFor">
    /// How long the claim survives without being released. Long enough to outlast the slowest
    /// realistic generation, short enough that an execution killed mid-flight costs the member
    /// one period's delay rather than the period itself.
    /// </param>
    /// <returns>
    /// The id of the claim taken, to hand back to <see cref="ReleaseAsync"/>, or null when
    /// another execution holds a live one. An ownership token rather than a convenience: a
    /// generation that overruns its lease is taken over by a later execution, and without a token
    /// the overrunning holder's release would delete its successor's claim and let a third
    /// execution in while the second was still working. A takeover mints a new id for exactly
    /// that reason, so a displaced holder's release matches nothing.
    /// </returns>
    Task<Guid?> TryClaimAsync(
        Guid cardiMemberId,
        GenerationWork work,
        DateOnly periodEnd,
        DateTime utcNow,
        TimeSpan heldFor,
        CancellationToken ct = default);

    /// <summary>
    /// Gives back the claim <paramref name="claimId"/> identifies, whether the attempt wrote
    /// anything or not. Releasing early is what lets the next pass retry a failed generation
    /// without waiting out the lease; a holder that never gets here is covered by the expiry.
    /// </summary>
    /// <remarks>
    /// Keyed on the claim, not on (member, work), so a holder whose lease has already been taken
    /// over releases nothing. Deleting by the pair would let a slow execution's <c>finally</c>
    /// remove a successor's live claim — the fencing problem, and the reason this takes a token.
    /// </remarks>
    Task ReleaseAsync(Guid claimId, CancellationToken ct = default);
}
