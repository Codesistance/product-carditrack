using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Notifications;

/// <summary>
/// Stands in for the gap resolver in tests of services that merely call it after a write.
/// </summary>
/// <remarks>
/// Doing nothing is faithful rather than lazy: synchronous resolution is an optimisation over the
/// detection worker, so a service's behaviour must not depend on what it returns. Calls are counted
/// so a test can still assert that a write path notified the engine at all.
/// </remarks>
public sealed class NoOpNotificationGapResolver : INotificationGapResolver
{
    public List<Guid> ResolvedMembers { get; } = [];
    public List<Guid> ResolvedUsers { get; } = [];
    public List<(Guid CardiMemberId, NotificationResolutionReason Reason)> Withdrawn { get; } = [];

    public Task ResolveForCardiMemberAsync(Guid cardiMemberId, CancellationToken ct = default)
    {
        ResolvedMembers.Add(cardiMemberId);
        return Task.CompletedTask;
    }

    public Task ResolveForUserAsync(Guid userId, CancellationToken ct = default)
    {
        ResolvedUsers.Add(userId);
        return Task.CompletedTask;
    }

    public Task WithdrawForCardiMemberAsync(
        Guid cardiMemberId, NotificationResolutionReason reason, CancellationToken ct = default)
    {
        Withdrawn.Add((cardiMemberId, reason));
        return Task.CompletedTask;
    }
}
