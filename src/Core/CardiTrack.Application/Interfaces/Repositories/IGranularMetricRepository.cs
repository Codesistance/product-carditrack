using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Interfaces.Repositories;

/// <summary>
/// The only read/write surface over the sub-daily tables (`GranularMetricHours`,
/// `MetricRollupsHourly`) — and deliberately so: per the granular-storage ADR
/// (docs/technical/granular_timeseries_storage.md), this interface is the entire migration
/// boundary if the store ever moves off Cloud SQL. The future GCP aggregator reads through it the
/// same way the pipeline reads tokens through the repository layer today.
/// <para>
/// Not an <see cref="IRepository{T}"/>: the tables are composite-keyed and partitioned, so the
/// Guid-keyed generic contract does not fit them.
/// </para>
/// </summary>
public interface IGranularMetricRepository
{
    /// <summary>
    /// Idempotently writes per-device hour vectors — a re-synced hour replaces its earlier self,
    /// the same upsert semantics ingestion applies to day rows.
    /// </summary>
    Task UpsertHoursAsync(IReadOnlyList<GranularMetricHour> hours, CancellationToken ct = default);

    /// <summary>Idempotently writes per-member hourly rollups (already merged across devices).</summary>
    Task UpsertRollupsAsync(IReadOnlyList<MetricRollupHourly> rollups, CancellationToken ct = default);

    /// <summary>
    /// The member's merged minute series over [<paramref name="fromUtc"/>, <paramref name="toUtc"/>),
    /// coalesced across devices by the standard priority order. Both bounds must be whole hours.
    /// </summary>
    Task<GranularWindow> GetWindowAsync(
        Guid cardiMemberId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>Hourly rollups for one metric over [from, to), oldest first.</summary>
    Task<IReadOnlyList<MetricRollupHourly>> GetRollupsAsync(
        Guid cardiMemberId, GranularMetric metric, DateTime fromUtc, DateTime toUtc,
        CancellationToken ct = default);
}
