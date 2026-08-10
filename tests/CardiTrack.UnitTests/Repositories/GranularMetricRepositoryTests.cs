using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using CardiTrack.UnitTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CardiTrack.UnitTests.Repositories;

/// <summary>
/// Runs against the real PostgreSQL container, which is the point: these tables are partitioned
/// parents created by raw migration SQL, and the upserts are raw <c>ON CONFLICT</c> statements —
/// none of which a substitute could vouch for. Every write test first runs the partition service,
/// exactly as production does, so a missing partition fails here and not in dev.
/// </summary>
[Collection("DatabaseCollection")]
public class GranularMetricRepositoryTests(TestDatabaseFixture fixture)
{
    /// <summary>A recent whole hour, safely inside the partition window the service creates.</summary>
    private static DateTime RecentHour => DateTime.UtcNow.Date.AddHours(-2);

    private static async Task EnsurePartitionsAsync(IServiceScope scope) =>
        await scope.ServiceProvider.GetRequiredService<ITimeSeriesPartitionService>()
            .EnsureUpcomingPartitionsAsync(daysAhead: 7);

    private static GranularMetricHour Hour(
        Guid memberId, Guid connectionId, DateTime hourStartUtc,
        GranularMetric metric = GranularMetric.HeartRate,
        params (int Minute, float Value)[] samples)
    {
        var row = new GranularMetricHour
        {
            CardiMemberId = memberId,
            DeviceConnectionId = connectionId,
            Metric = metric,
            HourStartUtc = hourStartUtc,
        };
        foreach (var (minute, value) in samples)
            row.Values[minute] = value;
        row.SampleCount = (short)samples.Length;
        return row;
    }

    [Fact]
    public async Task UpsertHoursAsync_ReplacesTheEarlierVector_ForTheSameHour()
    {
        using var scope = fixture.CreateScope();
        await EnsurePartitionsAsync(scope);
        var repo = scope.ServiceProvider.GetRequiredService<IGranularMetricRepository>();
        var memberId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();

        await repo.UpsertHoursAsync([Hour(memberId, connectionId, RecentHour, samples: [(0, 60f)])]);
        // The same hour re-synced with more data must replace, not duplicate or throw.
        await repo.UpsertHoursAsync([Hour(memberId, connectionId, RecentHour, samples: [(0, 61f), (1, 62f)])]);

        var window = await repo.GetWindowAsync(memberId, RecentHour, RecentHour.AddHours(1));
        var series = window.MinuteSeries[GranularMetric.HeartRate];
        Assert.Equal(61f, series[0]);
        Assert.Equal(62f, series[1]);
    }

    [Fact]
    public async Task GetWindowAsync_ThePrimaryDeviceWinsAMinute_BothReported()
    {
        using var scope = fixture.CreateScope();
        await EnsurePartitionsAsync(scope);
        var repo = scope.ServiceProvider.GetRequiredService<IGranularMetricRepository>();

        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
        var primary = await TestDataSeeder.SeedDeviceConnectionAsync(scope, member.Id, isPrimary: true);
        var secondary = await TestDataSeeder.SeedDeviceConnectionAsync(scope, member.Id);

        await repo.UpsertHoursAsync(
        [
            Hour(member.Id, secondary.Id, RecentHour, samples: [(0, 99f), (1, 68f)]),
            Hour(member.Id, primary.Id, RecentHour, samples: [(0, 70f)]),
        ]);

        var window = await repo.GetWindowAsync(member.Id, RecentHour, RecentHour.AddHours(1));

        var series = window.MinuteSeries[GranularMetric.HeartRate];
        Assert.Equal(70f, series[0]);  // primary wins the contested minute
        Assert.Equal(68f, series[1]);  // secondary fills the gap
    }

    [Fact]
    public async Task GetWindowAsync_PlacesEachHourAtItsOffset_AndLeavesGapsNull()
    {
        using var scope = fixture.CreateScope();
        await EnsurePartitionsAsync(scope);
        var repo = scope.ServiceProvider.GetRequiredService<IGranularMetricRepository>();
        var memberId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();

        var from = RecentHour.AddHours(-2);
        await repo.UpsertHoursAsync(
        [
            Hour(memberId, connectionId, from, samples: [(5, 71f)]),
            Hour(memberId, connectionId, from.AddHours(2), samples: [(10, 73f)]),
        ]);

        var window = await repo.GetWindowAsync(memberId, from, from.AddHours(3));

        var series = window.MinuteSeries[GranularMetric.HeartRate];
        Assert.Equal(180, series.Length);
        Assert.Equal(71f, series[5]);
        Assert.Null(series[65]);          // the hour with no row stays null
        Assert.Equal(73f, series[130]);
    }

    [Fact]
    public async Task GetWindowAsync_RejectsBoundsThatAreNotWholeHours()
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGranularMetricRepository>();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            repo.GetWindowAsync(Guid.NewGuid(), RecentHour.AddMinutes(30), RecentHour.AddHours(1)));
        // Sub-second offsets would pass a Minute/Second field check yet silently exclude the
        // first hour's row — the guard is tick-level for exactly that reason.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            repo.GetWindowAsync(Guid.NewGuid(), RecentHour.AddMilliseconds(500), RecentHour.AddHours(1)));
    }

    [Fact]
    public async Task Rollups_UpsertReplaces_AndReadBackComesOldestFirst()
    {
        using var scope = fixture.CreateScope();
        await EnsurePartitionsAsync(scope);
        var repo = scope.ServiceProvider.GetRequiredService<IGranularMetricRepository>();
        var memberId = Guid.NewGuid();

        MetricRollupHourly Rollup(DateTime hour, float avg) => new()
        {
            CardiMemberId = memberId,
            Metric = GranularMetric.Steps,
            HourStartUtc = hour,
            Min = avg - 1,
            Max = avg + 1,
            Avg = avg,
            Sum = avg * 60,
            SampleCount = 60,
        };

        await repo.UpsertRollupsAsync([Rollup(RecentHour, 50f), Rollup(RecentHour.AddHours(-1), 40f)]);
        await repo.UpsertRollupsAsync([Rollup(RecentHour, 55f)]);  // replaces, not duplicates

        var rollups = await repo.GetRollupsAsync(
            memberId, GranularMetric.Steps, RecentHour.AddHours(-1), RecentHour.AddHours(1));

        Assert.Equal(2, rollups.Count);
        Assert.Equal(40f, rollups[0].Avg);
        Assert.Equal(55f, rollups[1].Avg);
    }
}

/// <summary>
/// The partition lifecycle against the real catalog: creation must be idempotent, and the drop
/// path must destroy exactly the expired partitions it named itself — nothing else.
/// </summary>
[Collection("DatabaseCollection")]
public class TimeSeriesPartitionServiceTests(TestDatabaseFixture fixture)
{
    // The failure mode of a zero or negative retention is mass deletion of health data, so the
    // service refuses it outright (the worker independently skips the run on the same check).
    [Theory]
    [InlineData(0, 13, 12, 90)]
    [InlineData(90, 0, 12, 90)]
    [InlineData(90, 13, 0, 90)]
    [InlineData(90, 13, 12, 0)]
    [InlineData(-1, 13, 12, 90)]
    public async Task DropExpiredPartitionsAsync_RejectsNonPositiveRetention(
        int days, int months, int digestMonths, int realtimeDays)
    {
        using var scope = fixture.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITimeSeriesPartitionService>();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.DropExpiredPartitionsAsync(new PartitionRetention
            {
                GranularDays = days,
                RollupMonths = months,
                DigestMonths = digestMonths,
                RealtimeDays = realtimeDays,
            }));
    }

    [Fact]
    public async Task EnsureUpcomingPartitionsAsync_IsIdempotent()
    {
        using var scope = fixture.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITimeSeriesPartitionService>();

        await service.EnsureUpcomingPartitionsAsync(daysAhead: 7);
        await service.EnsureUpcomingPartitionsAsync(daysAhead: 7);  // must not throw

        var context = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var expected = TimeSeriesPartitions.DailyPartitionName(today);
        var exists = await context.Database
            .SqlQuery<string>($"""
                SELECT relname AS "Value" FROM pg_class WHERE relname = {expected}
                """)
            .AnyAsync();
        Assert.True(exists);
    }

    [Fact]
    public async Task DropExpiredPartitionsAsync_DropsExpired_AndLeavesForeignChildrenAlone()
    {
        using var scope = fixture.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITimeSeriesPartitionService>();
        var context = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        // An expired, correctly-named partition, and a child that does not follow the scheme.
        var expiredDay = new DateOnly(2020, 1, 1);
        await context.Database.ExecuteSqlRawAsync(TimeSeriesPartitions.CreateDailyPartitionSql(expiredDay));
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "GranularMetricHours_manual"
            PARTITION OF "GranularMetricHours"
            FOR VALUES FROM ('2020-06-01') TO ('2020-06-02');
            """);

        try
        {
            await service.DropExpiredPartitionsAsync(new PartitionRetention
            {
                GranularDays = 90,
                RollupMonths = 13,
                DigestMonths = 12,
                RealtimeDays = 90,
            });

            var survivors = await context.Database
                .SqlQuery<string>($"""
                    SELECT c.relname AS "Value"
                    FROM pg_inherits i
                    JOIN pg_class c ON c.oid = i.inhrelid
                    JOIN pg_class p ON p.oid = i.inhparent
                    WHERE p.relname = 'GranularMetricHours'
                    """)
                .ToListAsync();

            Assert.DoesNotContain(TimeSeriesPartitions.DailyPartitionName(expiredDay), survivors);
            // Never drop what we did not name — even though this child is long past retention.
            Assert.Contains("GranularMetricHours_manual", survivors);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync(
                """DROP TABLE IF EXISTS "GranularMetricHours_manual";""");
        }
    }
}
