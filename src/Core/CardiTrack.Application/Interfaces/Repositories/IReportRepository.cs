using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

public interface IReportRepository : IRepository<Report>
{
    /// <summary>
    /// One report, scoped to the user who asked for it. Ownership is applied in the query rather
    /// than by the caller, so there is no read path that can forget it — a report id is a
    /// bearer-style handle and someone else's report must read as "not found".
    /// Tracked, because the one writer reads then updates the same row.
    /// </summary>
    Task<Report?> GetForOwnerAsync(Guid reportId, Guid ownerUserId, CancellationToken ct = default);

    /// <summary>
    /// Reports whose <see cref="Report.ExpiresAt"/> has passed, oldest first — the cleanup
    /// worker's sweep. Batched: an unbounded sweep would hold a transaction open across an
    /// arbitrary number of bucket deletes.
    /// </summary>
    Task<IReadOnlyList<Report>> GetExpiredAsync(DateTime asOf, int limit, CancellationToken ct = default);

    /// <summary>
    /// Reports still <see cref="Domain.Enums.ReportStatus.Pending"/> past <paramref name="olderThan"/>
    /// — generations abandoned by a restart or a hung provider call. The cache-based design hid
    /// these behind its TTL; with durable rows they have to be failed out explicitly, or a
    /// caregiver polls a report that will never finish.
    /// </summary>
    Task<IReadOnlyList<Report>> GetStalePendingAsync(DateTime olderThan, int limit, CancellationToken ct = default);
}
