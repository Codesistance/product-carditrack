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
    Task<bool> TryClaimAsync(
        Guid cardiMemberId,
        GenerationWork work,
        DateOnly periodEnd,
        DateTime utcNow,
        TimeSpan heldFor,
        CancellationToken ct = default);

    /// <summary>
    /// Gives the claim back, whether the attempt wrote anything or not. Releasing early is what
    /// lets the next pass retry a failed generation without waiting out the lease; a holder that
    /// never gets here is covered by the expiry instead.
    /// </summary>
    Task ReleaseAsync(Guid cardiMemberId, GenerationWork work, CancellationToken ct = default);
}
