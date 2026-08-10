using CardiTrack.Application.Services.Notifications;

namespace CardiTrack.Application.Interfaces.Repositories;

/// <summary>
/// Builds the pre-fetched snapshots the rule catalogue evaluates against.
/// </summary>
/// <remarks>
/// Deliberately <b>set-based</b>. Coverage figures such as "distinct days with data in the last 30"
/// are computed as SQL aggregates — loading <c>ActivityLog</c> rows per member to count them in
/// memory would put the whole estate's daily history through the worker every morning.
/// </remarks>
public interface INotificationSnapshotQueries
{
    /// <summary>Ids of active organizations, ordered, for the worker's resumable batching.</summary>
    Task<IReadOnlyList<Guid>> GetActiveOrganizationIdsAsync(Guid? after, int take, CancellationToken ct = default);

    /// <summary>
    /// Every evaluation target in one organization: one context per (owner, member) pair, plus one
    /// member-less context per user for account-level rules.
    /// </summary>
    Task<IReadOnlyList<NudgeContext>> BuildContextsForOrganizationAsync(
        Guid organizationId, DateTime utcNow, CancellationToken ct = default);

    /// <summary>
    /// One user's contexts, for synchronous resolution after a write closes a gap.
    /// </summary>
    Task<IReadOnlyList<NudgeContext>> BuildContextsForUserAsync(
        Guid userId, DateTime utcNow, CancellationToken ct = default);

    /// <summary>Contexts for every user who owns nudges about this member.</summary>
    Task<IReadOnlyList<NudgeContext>> BuildContextsForCardiMemberAsync(
        Guid cardiMemberId, DateTime utcNow, CancellationToken ct = default);
}
