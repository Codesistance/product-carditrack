using CardiTrack.Application.Interfaces.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CardiTrack.Infrastructure.Persistence;

/// <summary>
/// Executes the partition lifecycle <see cref="TimeSeriesPartitions"/> describes. Creation is
/// idempotent (<c>IF NOT EXISTS</c>), so running often is safe and cheap — which is what lets the
/// maintenance worker run hourly and close the gap between a fresh deploy and its first partition.
/// Dropping a partition IS the retention mechanism: instant, and no dead tuples to vacuum.
/// </summary>
public class TimeSeriesPartitionService : ITimeSeriesPartitionService
{
    private readonly CardiTrackDbContext _context;
    private readonly ILogger<TimeSeriesPartitionService> _logger;

    public TimeSeriesPartitionService(
        CardiTrackDbContext context, ILogger<TimeSeriesPartitionService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task EnsureUpcomingPartitionsAsync(int daysAhead, CancellationToken ct = default)
    {
        // From yesterday, not today: a sync that starts just before UTC midnight can write into
        // the day that has just ended, and the partition for it must still exist.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var firstDay = today.AddDays(-1);
        var lastDay = today.AddDays(daysAhead);

        for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
            await _context.Database.ExecuteSqlRawAsync(TimeSeriesPartitions.CreateDailyPartitionSql(day), ct);

        var firstMonth = new DateOnly(firstDay.Year, firstDay.Month, 1);
        var lastMonth = new DateOnly(lastDay.Year, lastDay.Month, 1);
        for (var month = firstMonth; month <= lastMonth; month = month.AddMonths(1))
        {
            await _context.Database.ExecuteSqlRawAsync(TimeSeriesPartitions.CreateMonthlyPartitionSql(month), ct);
            await _context.Database.ExecuteSqlRawAsync(TimeSeriesPartitions.CreateDigestPartitionSql(month), ct);
        }
    }

    public async Task DropExpiredPartitionsAsync(
        int granularRetentionDays, int rollupRetentionMonths, int digestRetentionMonths,
        CancellationToken ct = default)
    {
        // A non-positive retention is always a misconfiguration, and the failure mode would be
        // mass deletion of health data — fail loud instead.
        if (granularRetentionDays <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(granularRetentionDays), granularRetentionDays, "Retention must be positive.");
        if (rollupRetentionMonths <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(rollupRetentionMonths), rollupRetentionMonths, "Retention must be positive.");
        if (digestRetentionMonths <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(digestRetentionMonths), digestRetentionMonths, "Retention must be positive.");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // "N days" means N *complete* days behind now, plus the day in progress — and a partition
        // is dropped only when its whole range is past that cutoff. The conservative boundary is
        // deliberate: retention here is a floor on availability, and destroying a partition that
        // still contains in-retention minutes would be the actual off-by-one.
        var granularCutoff = today.AddDays(-granularRetentionDays);
        foreach (var name in await ChildPartitionsAsync(TimeSeriesPartitions.GranularParent, ct))
        {
            if (TimeSeriesPartitions.TryParseDailyPartition(name, out var day)
                && day.AddDays(1) <= granularCutoff)
            {
                await DropAsync(name, ct);
            }
        }

        var rollupCutoff = new DateOnly(today.Year, today.Month, 1).AddMonths(-rollupRetentionMonths);
        foreach (var name in await ChildPartitionsAsync(TimeSeriesPartitions.RollupParent, ct))
        {
            if (TimeSeriesPartitions.TryParseMonthlyPartition(name, out var month)
                && month.AddMonths(1) <= rollupCutoff)
            {
                await DropAsync(name, ct);
            }
        }

        var digestCutoff = new DateOnly(today.Year, today.Month, 1).AddMonths(-digestRetentionMonths);
        foreach (var name in await ChildPartitionsAsync(TimeSeriesPartitions.DigestParent, ct))
        {
            if (TimeSeriesPartitions.TryParseDigestPartition(name, out var month)
                && month.AddMonths(1) <= digestCutoff)
            {
                await DropAsync(name, ct);
            }
        }
    }

    private async Task<List<string>> ChildPartitionsAsync(string parent, CancellationToken ct)
    {
        return await _context.Database
            .SqlQuery<string>($"""
                SELECT c.relname AS "Value"
                FROM pg_inherits i
                JOIN pg_class c ON c.oid = i.inhrelid
                JOIN pg_class p ON p.oid = i.inhparent
                WHERE p.relname = {parent}
                """)
            .ToListAsync(ct);
    }

    private async Task DropAsync(string partitionName, CancellationToken ct)
    {
        await _context.Database.ExecuteSqlRawAsync(TimeSeriesPartitions.DropPartitionSql(partitionName), ct);
        // Warning, deliberately: a drop destroys health data past retention — the log line is the
        // audit trail that retention ran, and when.
        _logger.LogWarning("Dropped expired time-series partition {Partition}.", partitionName);
    }
}
