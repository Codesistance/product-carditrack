namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// Lifecycle of the partitioned time-series tables. PostgreSQL has no TTL and does not create
/// range partitions on demand, so something must create them ahead of the data and drop them past
/// retention — that something is <c>PartitionMaintenanceWorker</c> in <c>CardiTrack.Worker</c>
/// (retention/cleanup is Worker-exclusive per CLAUDE.md); this port is what it drives.
/// </summary>
public interface ITimeSeriesPartitionService
{
    /// <summary>
    /// Creates (idempotently) the partitions covering yesterday through <paramref name="daysAhead"/>
    /// days from now, so an insert never lands in a missing partition.
    /// </summary>
    Task EnsureUpcomingPartitionsAsync(int daysAhead, CancellationToken ct = default);

    /// <summary>
    /// Drops partitions wholly past retention: granular hours after
    /// <paramref name="granularRetentionDays"/> days, hourly rollups after
    /// <paramref name="rollupRetentionMonths"/> months. Dropping a partition is the retention
    /// mechanism — instant, and no dead tuples to vacuum.
    /// </summary>
    Task DropExpiredPartitionsAsync(
        int granularRetentionDays, int rollupRetentionMonths, CancellationToken ct = default);
}
