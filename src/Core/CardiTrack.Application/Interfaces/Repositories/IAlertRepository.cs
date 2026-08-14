using CardiTrack.Application.DTOs.Common;
using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

public interface IAlertRepository : IRepository<Alert>
{
    /// <summary>
    /// Alerts for one member, newest first. Tracked, so producers can resolve or inspect
    /// history in place. Pass <paramref name="activeOnly"/> false when the same-data / same
    /// episode check must still see a caregiver-deleted row.
    /// </summary>
    Task<IEnumerable<Alert>> GetByCardiMemberAsync(Guid cardiMemberId, bool activeOnly);

    /// <summary>
    /// Active, unresolved alerts, newest first. Read-only — producers that resolve alerts must
    /// keep using <see cref="GetByCardiMemberAsync"/> so the entities stay tracked.
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
