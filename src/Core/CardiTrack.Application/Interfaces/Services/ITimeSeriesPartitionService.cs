namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// How long each partitioned time-series table keeps its data. A record with required named
/// members rather than a parameter list: two of these are day counts with the same default,
/// and a positional signature would let them transpose silently — the failure mode being mass
/// deletion of the wrong table's health data.
/// </summary>
public sealed record PartitionRetention
{
    /// <summary>Days of minute-grain metrics to keep.</summary>
    public required int GranularDays { get; init; }

    /// <summary>Months of hourly rollups to keep.</summary>
    public required int RollupMonths { get; init; }

    /// <summary>Months of daily digests to keep.</summary>
    public required int DigestMonths { get; init; }

    /// <summary>Days of real-time assessments to keep.</summary>
    public required int RealtimeDays { get; init; }

    /// <summary>Days of environmental readings to keep.</summary>
    public required int EnvironmentalDays { get; init; }
}

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
    /// Drops partitions wholly past <paramref name="retention"/>. Dropping a partition is the
    /// retention mechanism — instant, and no dead tuples to vacuum.
    /// </summary>
    Task DropExpiredPartitionsAsync(PartitionRetention retention, CancellationToken ct = default);
}
