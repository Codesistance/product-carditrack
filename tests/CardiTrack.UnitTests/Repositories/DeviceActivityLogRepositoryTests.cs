using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.UnitTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CardiTrack.UnitTests.Repositories;

[Collection("DatabaseCollection")]
public class DeviceActivityLogRepositoryTests(TestDatabaseFixture fixture)
{
    // ── UpsertAsync ──────────────────────────────────────────────────────────────
    //
    // These assert what UpdatedDate *means*, not merely that an upsert works. Sync re-fetches a
    // trailing window every run, so most upserts legitimately carry values the row already holds.
    // If those still stamped UpdatedDate, the column would record "when we last polled" and the
    // settle-latency and revision-tail figures derived from it would describe our own schedule
    // rather than the provider's behaviour.

    [Fact]
    public async Task UpsertAsync_InsertsRow_WhenNoneExistsForTheDeviceDay()
    {
        var (memberId, connectionId, date) = await SeedConnectionAsync();

        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceActivityLogRepository>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await repo.UpsertAsync(Log(memberId, connectionId, date, steps: 8000));
        await uow.SaveChangesAsync();

        var stored = (await repo.GetByCardiMemberAndDateAsync(memberId, date)).Single();
        Assert.Equal(8000, stored.Steps);
        Assert.Null(stored.UpdatedDate);
    }

    [Fact]
    public async Task UpsertAsync_LeavesUpdatedDateUntouched_WhenRefetchCarriesIdenticalValues()
    {
        var (memberId, connectionId, date) = await SeedConnectionAsync();
        await UpsertAsync(Log(memberId, connectionId, date, steps: 8000));

        // A separate scope, because that is how the real thing runs: one scope per sync, reading
        // the row back from the database rather than from a change tracker that still holds it.
        await UpsertAsync(Log(memberId, connectionId, date, steps: 8000));

        var stored = await GetSingleAsync(memberId, date);
        Assert.Null(stored.UpdatedDate);
    }

    [Fact]
    public async Task UpsertAsync_StampsUpdatedDate_WhenTheProviderRevisesAValue()
    {
        var (memberId, connectionId, date) = await SeedConnectionAsync();
        await UpsertAsync(Log(memberId, connectionId, date, steps: 8000));

        await UpsertAsync(Log(memberId, connectionId, date, steps: 8250));

        var stored = await GetSingleAsync(memberId, date);
        Assert.Equal(8250, stored.Steps);
        Assert.NotNull(stored.UpdatedDate);
    }

    [Fact]
    public async Task UpsertAsync_StampsUpdatedDate_WhenAMetricArrivesLate()
    {
        var (memberId, connectionId, date) = await SeedConnectionAsync();
        await UpsertAsync(Log(memberId, connectionId, date, steps: 8000, restingHeartRate: null));

        // Providers fill metrics in at different times; a null becoming a reading is a real change
        // and has to register as one, or a late-arriving metric would look like no change at all.
        await UpsertAsync(Log(memberId, connectionId, date, steps: 8000, restingHeartRate: 61));

        var stored = await GetSingleAsync(memberId, date);
        Assert.Equal(61, stored.RestingHeartRate);
        Assert.NotNull(stored.UpdatedDate);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private async Task<(Guid MemberId, Guid ConnectionId, DateOnly Date)> SeedConnectionAsync()
    {
        using var scope = fixture.CreateScope();
        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
        var connection = await TestDataSeeder.SeedDeviceConnectionAsync(scope, member.Id);

        return (member.Id, connection.Id, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)));
    }

    private async Task UpsertAsync(DeviceActivityLog log)
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceActivityLogRepository>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await repo.UpsertAsync(log);
        await uow.SaveChangesAsync();
    }

    private async Task<DeviceActivityLog> GetSingleAsync(Guid memberId, DateOnly date)
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceActivityLogRepository>();
        return (await repo.GetByCardiMemberAndDateAsync(memberId, date)).Single();
    }

    private static DeviceActivityLog Log(
        Guid memberId,
        Guid connectionId,
        DateOnly date,
        int? steps = 8000,
        int? restingHeartRate = 65) =>
        new()
        {
            Id = Guid.NewGuid(),
            CardiMemberId = memberId,
            DeviceConnectionId = connectionId,
            DataSource = DeviceType.Fitbit,
            Date = date,
            Steps = steps,
            Distance = 5.2m,
            ActiveMinutes = 45,
            SedentaryMinutes = 600,
            Floors = 10,
            CaloriesBurned = 2100,
            RestingHeartRate = restingHeartRate,
            AvgHeartRate = 72,
            MaxHeartRate = 120,
            MinHeartRate = 55,
            SleepMinutes = 450,
            SleepEfficiency = 87,
            DeepSleepMinutes = 90,
            LightSleepMinutes = 240,
            RemSleepMinutes = 90,
            AwakeMinutes = 30
        };
}
