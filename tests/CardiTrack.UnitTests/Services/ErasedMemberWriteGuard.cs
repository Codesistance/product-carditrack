using CardiTrack.Application.Interfaces.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// An <see cref="IMemberWriteGuard"/> that answers "this member has been erased" — the write does
/// not run and the caller is told so.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PassThroughWriteGuard"/>'s remarks say the refusal path is not unit-testable because
/// it is a claim about two concurrent transactions, and that is right about the <em>guard</em>.
/// It is not right about the callers: what a caller does with a <c>false</c> is ordinary control
/// flow, and it went untested long enough for <c>StatisticalAlertService</c> to discard the answer
/// entirely and keep walking a member the guard had just said was gone.
/// </para>
/// <para>
/// Deliberately not <c>Substitute.For&lt;IMemberWriteGuard&gt;()</c>, for the reason the
/// pass-through guard gives: a bare substitute also returns false, so a test using one would pass
/// for the wrong reason and every other test in the class would break far from its cause. This
/// says what it is in its name.
/// </para>
/// </remarks>
internal sealed class ErasedMemberWriteGuard : IMemberWriteGuard
{
    /// <summary>How many writes are let through before the member is treated as erased.</summary>
    private readonly int _writesBeforeErasure;

    private int _writes;

    internal ErasedMemberWriteGuard(int writesBeforeErasure = 0) =>
        _writesBeforeErasure = writesBeforeErasure;

    public async Task<bool> WriteIfMembersLiveAsync(
        IReadOnlyCollection<Guid> cardiMemberIds,
        Func<CancellationToken, Task> write,
        CancellationToken ct = default)
    {
        if (_writes++ >= _writesBeforeErasure)
            return false;

        await write(ct);
        return true;
    }

    public Task<bool> HoldMembersAsync(
        IReadOnlyCollection<Guid> cardiMemberIds, CancellationToken ct = default) =>
        Task.FromResult(false);
}
