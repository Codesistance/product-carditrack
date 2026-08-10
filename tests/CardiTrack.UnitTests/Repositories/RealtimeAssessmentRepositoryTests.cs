using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.UnitTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CardiTrack.UnitTests.Repositories;

/// <summary>
/// Against the real PostgreSQL container, for the same reason as the granular tests: the table
/// is a partitioned parent created by raw migration SQL and the upsert is a raw
/// <c>ON CONFLICT</c> statement — a substitute could vouch for neither. Every write first runs
/// the partition service, exactly as production does.
/// </summary>
[Collection("DatabaseCollection")]
public class RealtimeAssessmentRepositoryTests(TestDatabaseFixture fixture)
{
    /// <summary>A recent whole hour, safely inside the partition window the service creates.</summary>
    private static DateTime RecentHour => DateTime.UtcNow.Date.AddHours(-2);

    private static async Task EnsurePartitionsAsync(IServiceScope scope) =>
        await scope.ServiceProvider.GetRequiredService<ITimeSeriesPartitionService>()
            .EnsureUpcomingPartitionsAsync(daysAhead: 7);

    private static RealtimeAssessment Assessment(
        Guid memberId, DateTime windowStartUtc, AlertSeverity? severity = AlertSeverity.Green,
        string output = "A steady hour.") => new()
        {
            CardiMemberId = memberId,
            WindowStartUtc = windowStartUtc,
            WindowEndUtc = windowStartUtc.AddMinutes(60),
            HrTrendLast = 71.5,
            HrDeviationScore = 0.8,
            HrNoiseRms = 1.2,
            StepsSum = 340,
            SpO2Mean = null,
            ModelOutput = output,
            RawSeverity = severity switch
            {
                AlertSeverity.Red => "critical",
                AlertSeverity.Orange => "high",
                AlertSeverity.Yellow => "medium",
                AlertSeverity.Green => "low",
                _ => null,
            },
            Severity = severity,
            GeneratedAtUtc = DateTime.UtcNow,
        };

    // The return value is the concurrency arbiter for severity routing (only the inserter may
    // alert), so true-then-false is behavior the service depends on — not a nicety.
    [Fact]
    public async Task UpsertAsync_ReplacesTheEarlierRow_AndReportsInsertVersusUpdate()
    {
        using var scope = fixture.CreateScope();
        await EnsurePartitionsAsync(scope);
        var repo = scope.ServiceProvider.GetRequiredService<IRealtimeAssessmentRepository>();
        var memberId = Guid.NewGuid();

        var first = await repo.UpsertAsync(Assessment(memberId, RecentHour, AlertSeverity.Green, "First pass."));
        var second = await repo.UpsertAsync(Assessment(memberId, RecentHour, AlertSeverity.Orange, "Second pass."));

        Assert.True(first);
        Assert.False(second);

        var stored = await repo.GetLatestAsync(memberId);
        Assert.NotNull(stored);
        Assert.Equal("Second pass.", stored.ModelOutput);
        Assert.Equal(AlertSeverity.Orange, stored.Severity);
        Assert.Equal("high", stored.RawSeverity);
    }

    // The null-severity row is the audit trail of an unparseable model answer — it must store
    // and read back as genuinely null, not as a default.
    [Fact]
    public async Task ANullSeverity_RoundTrips()
    {
        using var scope = fixture.CreateScope();
        await EnsurePartitionsAsync(scope);
        var repo = scope.ServiceProvider.GetRequiredService<IRealtimeAssessmentRepository>();
        var memberId = Guid.NewGuid();

        await repo.UpsertAsync(Assessment(memberId, RecentHour, severity: null, "No label."));

        var stored = await repo.GetLatestAsync(memberId);
        Assert.NotNull(stored);
        Assert.Null(stored.Severity);
        Assert.Null(stored.RawSeverity);
    }

    [Fact]
    public async Task ExistsAsync_SeesExactlyTheWrittenWindow()
    {
        using var scope = fixture.CreateScope();
        await EnsurePartitionsAsync(scope);
        var repo = scope.ServiceProvider.GetRequiredService<IRealtimeAssessmentRepository>();
        var memberId = Guid.NewGuid();

        await repo.UpsertAsync(Assessment(memberId, RecentHour));

        Assert.True(await repo.ExistsAsync(memberId, RecentHour));
        Assert.False(await repo.ExistsAsync(memberId, RecentHour.AddMinutes(5)));
        Assert.False(await repo.ExistsAsync(Guid.NewGuid(), RecentHour));
    }

    [Fact]
    public async Task GetLatestAsync_PicksTheNewestWindow_PerMember()
    {
        using var scope = fixture.CreateScope();
        await EnsurePartitionsAsync(scope);
        var repo = scope.ServiceProvider.GetRequiredService<IRealtimeAssessmentRepository>();
        var memberId = Guid.NewGuid();

        await repo.UpsertAsync(Assessment(memberId, RecentHour.AddHours(-1), output: "Older."));
        await repo.UpsertAsync(Assessment(memberId, RecentHour, output: "Newer."));
        await repo.UpsertAsync(Assessment(Guid.NewGuid(), RecentHour.AddMinutes(30), output: "Someone else."));

        var latest = await repo.GetLatestAsync(memberId);
        Assert.NotNull(latest);
        Assert.Equal("Newer.", latest.ModelOutput);
    }
}
