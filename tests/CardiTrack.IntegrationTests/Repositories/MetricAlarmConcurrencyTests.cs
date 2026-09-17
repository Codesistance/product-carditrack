using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace CardiTrack.IntegrationTests.Repositories;

/// <summary>
/// An alarm's row version is its concurrency token, against a real Postgres: a write from a read
/// that a later commit has overtaken is refused rather than applied over it. This is what closes
/// the last window in the chat's stale-proposal check — the fingerprint is compared on the rows
/// the service has read, and the UPDATE itself is predicated on those rows being unchanged.
/// </summary>
public class MetricAlarmConcurrencyTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithCleanUp(true)
        .Build();

    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var sc = new ServiceCollection();
        sc.AddDbContext<CardiTrackDbContext>(options =>
            options.UseNpgsql(_container.GetConnectionString(),
                b => b.MigrationsAssembly("CardiTrack.Infrastructure")));
        _services = sc.BuildServiceProvider();

        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>().Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _container.DisposeAsync();
    }

    private async Task<Guid> SeedAlarmAsync()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        var alarm = new MetricAlarm
        {
            OrganizationId = Guid.NewGuid(),
            CardiMemberId = Guid.NewGuid(),
            Name = "High heart rate",
            Metric = AlarmMetric.HeartRate,
            Statistic = AlarmStatistic.Average,
            Operator = AlarmOperator.GreaterThan,
            ThresholdKind = AlarmThresholdKind.Absolute,
            ThresholdValue = 120,
            PeriodMinutes = 5,
            EvaluationPeriods = 2,
            DatapointsToAlarm = 2,
            Severity = AlertSeverity.Yellow,
            IsEnabled = true,
        };
        db.MetricAlarms.Add(alarm);
        await db.SaveChangesAsync();
        return alarm.Id;
    }

    /// <summary>Two caregivers, two reads; the second commit is refused because the row it read
    /// is no longer the row in the table.</summary>
    [Fact]
    public async Task AWriteFromAnOvertakenRead_IsRefused()
    {
        var alarmId = await SeedAlarmAsync();

        using var firstScope = _services.CreateScope();
        using var secondScope = _services.CreateScope();
        var first = firstScope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        var second = secondScope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        var seenByFirst = await first.MetricAlarms.SingleAsync(a => a.Id == alarmId);
        var seenBySecond = await second.MetricAlarms.SingleAsync(a => a.Id == alarmId);

        seenBySecond.ThresholdValue = 125;
        await second.SaveChangesAsync();

        seenByFirst.ThresholdValue = 130;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => first.SaveChangesAsync());

        using var readScope = _services.CreateScope();
        var stored = await readScope.ServiceProvider.GetRequiredService<CardiTrackDbContext>()
            .MetricAlarms.AsNoTracking().SingleAsync(a => a.Id == alarmId);
        Assert.Equal(125, stored.ThresholdValue);
    }

    [Fact]
    public async Task AWriteFromACurrentRead_Proceeds()
    {
        var alarmId = await SeedAlarmAsync();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        var alarm = await db.MetricAlarms.SingleAsync(a => a.Id == alarmId);
        alarm.ThresholdValue = 130;
        await db.SaveChangesAsync();

        // And the same context can write again: the token moved with the commit.
        alarm.Name = "Racing heart";
        await db.SaveChangesAsync();
    }
}
