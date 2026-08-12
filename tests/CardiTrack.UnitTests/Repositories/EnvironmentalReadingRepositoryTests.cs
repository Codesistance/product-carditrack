using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.UnitTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CardiTrack.UnitTests.Repositories;

/// <summary>
/// Against the real PostgreSQL container, for the same reason as
/// <c>RealtimeAssessmentRepositoryTests</c>: the table is a partitioned parent created by raw
/// migration SQL and the upsert is a raw <c>ON CONFLICT</c> statement — a substitute could vouch
/// for neither.
/// </summary>
[Collection("DatabaseCollection")]
public class EnvironmentalReadingRepositoryTests(TestDatabaseFixture fixture)
{
    private static DateTime RecentSession => DateTime.UtcNow.Date.AddHours(-2);

    private static async Task EnsurePartitionsAsync(IServiceScope scope) =>
        await scope.ServiceProvider.GetRequiredService<ITimeSeriesPartitionService>()
            .EnsureUpcomingPartitionsAsync(daysAhead: 7);

    private static EnvironmentalReading Reading(
        Guid memberId, DateTime sessionStartUtc, double? temperature = 21.0, int? aqi = 30) => new()
        {
            CardiMemberId = memberId,
            SessionStartUtc = sessionStartUtc,
            SessionEndUtc = sessionStartUtc.AddMinutes(30),
            DeviceConnectionId = Guid.NewGuid(),
            TemperatureCelsius = temperature,
            AirQualityIndex = aqi,
            AirQualityCategory = aqi is null ? null : "Good",
            GeneratedAtUtc = DateTime.UtcNow,
        };

    [Fact]
    public async Task UpsertAsync_ReplacesTheEarlierRow_AndReportsInsertVersusUpdate()
    {
        using var scope = fixture.CreateScope();
        await EnsurePartitionsAsync(scope);
        var repo = scope.ServiceProvider.GetRequiredService<IEnvironmentalReadingRepository>();
        var memberId = Guid.NewGuid();

        var first = await repo.UpsertAsync(Reading(memberId, RecentSession, temperature: 18.0));
        var second = await repo.UpsertAsync(Reading(memberId, RecentSession, temperature: 24.5));

        Assert.True(first);
        Assert.False(second);

        var stored = await repo.GetLatestAsync(memberId);
        Assert.NotNull(stored);
        Assert.Equal(24.5, stored.TemperatureCelsius);
    }

    [Fact]
    public async Task NoCoordinateColumnsExistOnTheStoredRow()
    {
        // Structural check on the entity itself, not just this test's fixture data: the whole
        // privacy mitigation this feature relies on is that a coordinate is never a persisted
        // field, so this asserts the *type* carries none — not merely that this test forgot to
        // set one.
        var properties = typeof(EnvironmentalReading).GetProperties();
        Assert.DoesNotContain(properties, p =>
            p.Name.Contains("Latitude", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Contains("Longitude", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Contains("Coordinate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExistsAsync_SeesExactlyTheWrittenSession()
    {
        using var scope = fixture.CreateScope();
        await EnsurePartitionsAsync(scope);
        var repo = scope.ServiceProvider.GetRequiredService<IEnvironmentalReadingRepository>();
        var memberId = Guid.NewGuid();

        await repo.UpsertAsync(Reading(memberId, RecentSession));

        Assert.True(await repo.ExistsAsync(memberId, RecentSession));
        Assert.False(await repo.ExistsAsync(memberId, RecentSession.AddMinutes(5)));
        Assert.False(await repo.ExistsAsync(Guid.NewGuid(), RecentSession));
    }

    [Fact]
    public async Task GetLatestAsync_PicksTheNewestSession_PerMember()
    {
        using var scope = fixture.CreateScope();
        await EnsurePartitionsAsync(scope);
        var repo = scope.ServiceProvider.GetRequiredService<IEnvironmentalReadingRepository>();
        var memberId = Guid.NewGuid();

        await repo.UpsertAsync(Reading(memberId, RecentSession.AddHours(-1), temperature: 10.0));
        await repo.UpsertAsync(Reading(memberId, RecentSession, temperature: 20.0));
        await repo.UpsertAsync(Reading(Guid.NewGuid(), RecentSession.AddMinutes(30), temperature: 99.0));

        var latest = await repo.GetLatestAsync(memberId);
        Assert.NotNull(latest);
        Assert.Equal(20.0, latest.TemperatureCelsius);
    }
}
