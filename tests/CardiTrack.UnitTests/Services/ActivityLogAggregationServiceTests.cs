using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.UnitTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CardiTrack.UnitTests.Services;

[Collection("DatabaseCollection")]
public class ActivityLogAggregationServiceTests(TestDatabaseFixture fixture)
{
    private static readonly DateOnly Date = new(2026, 8, 6);

    private static DeviceActivityLog Raw(
        Guid memberId,
        Guid connectionId,
        int? steps = null,
        int? restingHeartRate = null,
        int? sleepMinutes = null,
        decimal? spO2Average = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            CardiMemberId = memberId,
            DeviceConnectionId = connectionId,
            DataSource = DeviceType.Fitbit,
            Date = Date,
            Steps = steps,
            RestingHeartRate = restingHeartRate,
            SleepMinutes = sleepMinutes,
            SpO2Average = spO2Average
        };

    [Fact]
    public async Task RecomputeAsync_MergesBothDevices_IntoOneRow()
    {
        using var scope = fixture.CreateScope();
        var raw = scope.ServiceProvider.GetRequiredService<IDeviceActivityLogRepository>();
        var merged = scope.ServiceProvider.GetRequiredService<IActivityLogRepository>();
        var aggregation = scope.ServiceProvider.GetRequiredService<IActivityLogAggregationService>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
        var watch = await TestDataSeeder.SeedDeviceConnectionAsync(scope, member.Id, isPrimary: true);
        var ring = await TestDataSeeder.SeedDeviceConnectionAsync(scope, member.Id);

        await raw.UpsertAsync(Raw(member.Id, watch.Id, steps: 8000, restingHeartRate: 65));
        await raw.UpsertAsync(Raw(member.Id, ring.Id, steps: 7900, sleepMinutes: 420, spO2Average: 96.5m));
        await uow.SaveChangesAsync();

        await aggregation.RecomputeAsync(member.Id, Date);
        await uow.SaveChangesAsync();

        var results = await merged.GetByCardiMemberAndDateRangeAsync(member.Id, Date, Date);
        var row = Assert.Single(results);
        Assert.Equal(8000, row.Steps);          // primary device wins
        Assert.Equal(65, row.RestingHeartRate); // primary device wins
        Assert.Equal(420, row.SleepMinutes);    // gap filled from the ring
        Assert.Equal(96.5m, row.SpO2Average);   // gap filled from the ring
        Assert.Equal(watch.Id, row.DeviceConnectionId);
    }

    // The whole point of rebuilding from the raw set: running it twice must not drift or duplicate.
    [Fact]
    public async Task RecomputeAsync_IsIdempotent()
    {
        using var scope = fixture.CreateScope();
        var raw = scope.ServiceProvider.GetRequiredService<IDeviceActivityLogRepository>();
        var merged = scope.ServiceProvider.GetRequiredService<IActivityLogRepository>();
        var aggregation = scope.ServiceProvider.GetRequiredService<IActivityLogAggregationService>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
        var watch = await TestDataSeeder.SeedDeviceConnectionAsync(scope, member.Id, isPrimary: true);

        await raw.UpsertAsync(Raw(member.Id, watch.Id, steps: 8000));
        await uow.SaveChangesAsync();

        for (var i = 0; i < 3; i++)
        {
            await aggregation.RecomputeAsync(member.Id, Date);
            await uow.SaveChangesAsync();
        }

        var results = await merged.GetByCardiMemberAndDateRangeAsync(member.Id, Date, Date);
        var row = Assert.Single(results);
        Assert.Equal(8000, row.Steps);
    }

    // A device's raw row changing must be reflected — the merged row is derived, not append-only.
    [Fact]
    public async Task RecomputeAsync_ReflectsAnUpdatedRawRow()
    {
        using var scope = fixture.CreateScope();
        var raw = scope.ServiceProvider.GetRequiredService<IDeviceActivityLogRepository>();
        var merged = scope.ServiceProvider.GetRequiredService<IActivityLogRepository>();
        var aggregation = scope.ServiceProvider.GetRequiredService<IActivityLogAggregationService>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
        var watch = await TestDataSeeder.SeedDeviceConnectionAsync(scope, member.Id, isPrimary: true);

        await raw.UpsertAsync(Raw(member.Id, watch.Id, steps: 8000));
        await uow.SaveChangesAsync();
        await aggregation.RecomputeAsync(member.Id, Date);
        await uow.SaveChangesAsync();

        // Provider finalised the day with a corrected figure
        await raw.UpsertAsync(Raw(member.Id, watch.Id, steps: 9500));
        await uow.SaveChangesAsync();
        await aggregation.RecomputeAsync(member.Id, Date);
        await uow.SaveChangesAsync();

        var results = await merged.GetByCardiMemberAndDateRangeAsync(member.Id, Date, Date);
        var row = Assert.Single(results);
        Assert.Equal(9500, row.Steps);
    }

    [Fact]
    public async Task RecomputeAsync_DoesNothing_WhenTheMemberHasNoRawRowsForTheDay()
    {
        using var scope = fixture.CreateScope();
        var merged = scope.ServiceProvider.GetRequiredService<IActivityLogRepository>();
        var aggregation = scope.ServiceProvider.GetRequiredService<IActivityLogAggregationService>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);

        await aggregation.RecomputeAsync(member.Id, Date);
        await uow.SaveChangesAsync();

        var results = await merged.GetByCardiMemberAndDateRangeAsync(member.Id, Date, Date);
        Assert.Empty(results);
    }

    // Changing which device is primary must change the merge on the next recompute.
    [Fact]
    public async Task RecomputeAsync_FollowsTheCurrentPrimaryDevice()
    {
        using var scope = fixture.CreateScope();
        var raw = scope.ServiceProvider.GetRequiredService<IDeviceActivityLogRepository>();
        var merged = scope.ServiceProvider.GetRequiredService<IActivityLogRepository>();
        var connections = scope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();
        var aggregation = scope.ServiceProvider.GetRequiredService<IActivityLogAggregationService>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
        var watch = await TestDataSeeder.SeedDeviceConnectionAsync(scope, member.Id, isPrimary: true);
        var ring = await TestDataSeeder.SeedDeviceConnectionAsync(scope, member.Id);

        await raw.UpsertAsync(Raw(member.Id, watch.Id, steps: 8000));
        await raw.UpsertAsync(Raw(member.Id, ring.Id, steps: 7900));
        await uow.SaveChangesAsync();

        await aggregation.RecomputeAsync(member.Id, Date);
        await uow.SaveChangesAsync();

        var before = (await merged.GetByCardiMemberAndDateRangeAsync(member.Id, Date, Date)).Single();
        Assert.Equal(8000, before.Steps);

        // Promote the ring
        var watchEntity = await connections.GetByIdAsync(watch.Id);
        var ringEntity = await connections.GetByIdAsync(ring.Id);
        watchEntity!.IsPrimary = false;
        ringEntity!.IsPrimary = true;
        await uow.SaveChangesAsync();

        await aggregation.RecomputeAsync(member.Id, Date);
        await uow.SaveChangesAsync();

        var after = (await merged.GetByCardiMemberAndDateRangeAsync(member.Id, Date, Date)).Single();
        Assert.Equal(7900, after.Steps);
        Assert.Equal(ring.Id, after.DeviceConnectionId);
    }
}
