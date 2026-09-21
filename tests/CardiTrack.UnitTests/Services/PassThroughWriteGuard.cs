using CardiTrack.Application.Interfaces.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// An <see cref="IMemberWriteGuard"/> that always lets the write through.
/// </summary>
/// <remarks>
/// The real guard takes a PostgreSQL row lock, which these tests have no database to take. What
/// they are about is what each generator produces, not whether it may store it, so the guard is
/// replaced by the "yes" answer it gives whenever the member exists — which is every case here.
/// The refusal path is not unit-testable and is not tested here: it is a claim about two concurrent
/// transactions, and it is proven in <c>ErasureDuringGenerationTests</c> against a real Postgres.
///
/// Deliberately not <c>Substitute.For&lt;IMemberWriteGuard&gt;()</c>: a substitute returns false and
/// never invokes the write, so every generator would silently stop persisting and the tests would
/// fail somewhere far from the cause.
/// </remarks>
internal sealed class PassThroughWriteGuard : IMemberWriteGuard
{
    public async Task<bool> WriteIfMembersLiveAsync(
        IReadOnlyCollection<Guid> cardiMemberIds,
        Func<CancellationToken, Task> write,
        CancellationToken ct = default)
    {
        await write(ct);
        return true;
    }

    // No transaction requirement here, unlike the real guard: these tests reach the callers that
    // take a hold without opening one, and throwing would fail them for the absence of a lock they
    // have no database to take.
    public Task<bool> HoldMembersAsync(
        IReadOnlyCollection<Guid> cardiMemberIds, CancellationToken ct = default) =>
        Task.FromResult(true);
}
