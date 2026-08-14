using CardiTrack.Application.DTOs.Common;
using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

public interface IAlertRepository : IRepository<Alert>
{
    Task<IEnumerable<Alert>> GetByCardiMemberAsync(Guid cardiMemberId, bool activeOnly);

    /// <summary>
    /// Newest active alerts, capped in SQL. Read-only — producers that resolve alerts must
    /// keep using <see cref="GetByCardiMemberAsync"/> so the entities stay tracked.
    /// </summary>
    Task<IReadOnlyList<Alert>> GetRecentByCardiMemberAsync(Guid cardiMemberId, int limit);

    /// <summary>
    /// Active, unresolved alerts. Read-only, same tracking rule as
    /// <see cref="GetRecentByCardiMemberAsync"/>.
    /// </summary>
    Task<IReadOnlyList<Alert>> GetUnresolvedByCardiMemberAsync(Guid cardiMemberId);

    Task<Alert?> GetByIdWithCardiMemberAsync(Guid alertId);

    /// <summary>One page of alerts matching <paramref name="query"/>, newest first.</summary>
    Task<IReadOnlyList<Alert>> QueryAsync(AlertQuery query, CancellationToken ct = default);

    /// <summary>How many alerts match <paramref name="query"/>'s filters, ignoring its paging.</summary>
    Task<int> CountAsync(AlertQuery query, CancellationToken ct = default);

    /// <summary>
    /// Unacknowledged, unresolved alerts across <paramref name="cardiMemberIds"/> — the badge
    /// count, so deliberately unaffected by the caller's severity/date filters.
    /// </summary>
    Task<int> CountUnreadAsync(IReadOnlyCollection<Guid> cardiMemberIds, CancellationToken ct = default);
}
