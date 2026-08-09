using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

/// <summary>
/// Cloud SQL implementation over the partitioned time-series tables. Writes go through raw
/// <c>ON CONFLICT</c> upserts (EF has no native upsert, and a re-synced hour must replace its
/// earlier self); reads use LINQ over the composite-key index and merge across devices in memory
/// via <see cref="GranularSeriesMerge"/>.
/// </summary>
public class GranularMetricRepository : IGranularMetricRepository
{
    private readonly CardiTrackDbContext _context;

    public GranularMetricRepository(CardiTrackDbContext context)
    {
        _context = context;
    }

    public async Task UpsertHoursAsync(
        IReadOnlyList<GranularMetricHour> hours, CancellationToken ct = default)
    {
        // Row-at-a-time is deliberate: a sync slice touches at most a handful of hours per
        // metric, so a multi-row UNNEST would add complexity without a measurable win.
        foreach (var hour in hours)
        {
            await _context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "GranularMetricHours"
                    ("DeviceConnectionId", "CardiMemberId", "Metric", "HourStartUtc", "Values", "SampleCount")
                VALUES ({hour.DeviceConnectionId}, {hour.CardiMemberId}, {hour.Metric.ToString()},
                        {hour.HourStartUtc}, {hour.Values}, {hour.SampleCount})
                ON CONFLICT ("DeviceConnectionId", "Metric", "HourStartUtc")
                DO UPDATE SET
                    "CardiMemberId" = EXCLUDED."CardiMemberId",
                    "Values" = EXCLUDED."Values",
                    "SampleCount" = EXCLUDED."SampleCount"
                """, ct);
        }
    }

    public async Task UpsertRollupsAsync(
        IReadOnlyList<MetricRollupHourly> rollups, CancellationToken ct = default)
    {
        foreach (var rollup in rollups)
        {
            await _context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "MetricRollupsHourly"
                    ("CardiMemberId", "Metric", "HourStartUtc", "Min", "Max", "Avg", "Sum", "SampleCount")
                VALUES ({rollup.CardiMemberId}, {rollup.Metric.ToString()}, {rollup.HourStartUtc},
                        {rollup.Min}, {rollup.Max}, {rollup.Avg}, {rollup.Sum}, {rollup.SampleCount})
                ON CONFLICT ("CardiMemberId", "Metric", "HourStartUtc")
                DO UPDATE SET
                    "Min" = EXCLUDED."Min",
                    "Max" = EXCLUDED."Max",
                    "Avg" = EXCLUDED."Avg",
                    "Sum" = EXCLUDED."Sum",
                    "SampleCount" = EXCLUDED."SampleCount"
                """, ct);
        }
    }

    public async Task<GranularWindow> GetWindowAsync(
        Guid cardiMemberId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        if (fromUtc >= toUtc)
            throw new ArgumentOutOfRangeException(nameof(toUtc), toUtc, "Window must not be empty.");
        if (fromUtc.Minute != 0 || fromUtc.Second != 0 || toUtc.Minute != 0 || toUtc.Second != 0)
            throw new ArgumentException("Window bounds must be whole hours (see IGranularMetricRepository).");

        var rows = await _context.GranularMetricHours
            .AsNoTracking()
            .Where(g => g.CardiMemberId == cardiMemberId
                        && g.HourStartUtc >= fromUtc
                        && g.HourStartUtc < toUtc)
            .ToListAsync(ct);

        var priorityRank = await DevicePriorityRankAsync(cardiMemberId, ct);

        var totalMinutes = (int)(toUtc - fromUtc).TotalMinutes;
        var series = new Dictionary<GranularMetric, float?[]>();

        foreach (var hourGroup in rows.GroupBy(g => new { g.Metric, g.HourStartUtc }))
        {
            // Rows whose connection has since been removed rank last, id-tiebroken — the same
            // "deleting a device never silently drops history" rule the daily merge applies.
            var byPriority = hourGroup
                .OrderBy(g => priorityRank.GetValueOrDefault(g.DeviceConnectionId, int.MaxValue))
                .ThenBy(g => g.DeviceConnectionId)
                .ToList();

            var merged = GranularSeriesMerge.MergeHour(byPriority);

            if (!series.TryGetValue(hourGroup.Key.Metric, out var target))
            {
                target = new float?[totalMinutes];
                series[hourGroup.Key.Metric] = target;
            }

            var offset = (int)(hourGroup.Key.HourStartUtc - fromUtc).TotalMinutes;
            Array.Copy(merged, 0, target, offset, GranularMetricHour.MinutesPerHour);
        }

        return new GranularWindow
        {
            CardiMemberId = cardiMemberId,
            FromUtc = fromUtc,
            ToUtc = toUtc,
            MinuteSeries = series,
        };
    }

    public async Task<IReadOnlyList<MetricRollupHourly>> GetRollupsAsync(
        Guid cardiMemberId, GranularMetric metric, DateTime fromUtc, DateTime toUtc,
        CancellationToken ct = default)
    {
        return await _context.MetricRollupsHourly
            .AsNoTracking()
            .Where(r => r.CardiMemberId == cardiMemberId
                        && r.Metric == metric
                        && r.HourStartUtc >= fromUtc
                        && r.HourStartUtc < toUtc)
            .OrderBy(r => r.HourStartUtc)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Connection id → merge rank, from the same ordering the daily merge uses
    /// (<see cref="ActivityLogMerge.ByPriority"/>), so minute and day merges can never disagree
    /// about which device wins.
    /// </summary>
    private async Task<Dictionary<Guid, int>> DevicePriorityRankAsync(
        Guid cardiMemberId, CancellationToken ct)
    {
        var connections = await _context.DeviceConnections
            .AsNoTracking()
            .Where(c => c.CardiMemberId == cardiMemberId)
            .ToListAsync(ct);

        return ActivityLogMerge.ByPriority(connections)
            .Select((connection, rank) => (connection.Id, rank))
            .ToDictionary(pair => pair.Id, pair => pair.rank);
    }
}
