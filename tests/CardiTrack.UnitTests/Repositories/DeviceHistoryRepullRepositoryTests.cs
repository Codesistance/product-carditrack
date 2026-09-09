using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.UnitTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CardiTrack.UnitTests.Repositories;

/// <summary>
/// Against the real PostgreSQL container, because every claim these queries make is a claim about
/// SQL: the latest-per-connection read picks its row in the database rather than in memory, the
/// one-open-per-connection rule is a partial unique index rather than a check the service does,
/// and the due read's ordering is what decides whose re-pull runs next. A substitute could vouch
/// for none of that — and a query that fails to translate fails only at runtime.
/// </summary>
[Collection("DatabaseCollection")]
public class DeviceHistoryRepullRepositoryTests(TestDatabaseFixture fixture)
{
    private static DeviceHistoryRepull Repull(
        Guid connectionId,
        Guid memberId,
        DateTime requestedAt,
        HistoryRepullStatus status = HistoryRepullStatus.Pending,
        DateTime? completedAt = null) => new()
        {
            Id = Guid.NewGuid(),
            DeviceConnectionId = connectionId,
            CardiMemberId = memberId,
            RequestedByUserId = Guid.NewGuid(),
            FromDate = new DateOnly(2026, 8, 10),
            ToDate = new DateOnly(2026, 9, 8),
            Status = status,
            RequestedAt = requestedAt,
            CompletedAt = completedAt,
        };

    private static async Task<IDeviceHistoryRepullRepository> SaveAsync(
        IServiceScope scope, params DeviceHistoryRepull[] repulls)
    {
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceHistoryRepullRepository>();
        foreach (var repull in repulls)
            await repo.AddAsync(repull);

        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        return repo;
    }

    [Fact]
    public async Task GetLatestByConnectionIds_ReturnsOneRowPerConnection_TheNewestOne()
    {
        using var scope = fixture.CreateScope();
        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
        var first = await TestDataSeeder.SeedDeviceConnectionAsync(scope, member.Id);
        var second = await TestDataSeeder.SeedDeviceConnectionAsync(scope, member.Id);

        var newest = Repull(first.Id, member.Id, new DateTime(2026, 9, 9, 10, 0, 0, DateTimeKind.Utc),
            HistoryRepullStatus.Completed, completedAt: new DateTime(2026, 9, 9, 11, 0, 0, DateTimeKind.Utc));
        var repo = await SaveAsync(
            scope,
            Repull(first.Id, member.Id, new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc),
                HistoryRepullStatus.Completed, completedAt: new DateTime(2026, 9, 1, 11, 0, 0, DateTimeKind.Utc)),
            Repull(first.Id, member.Id, new DateTime(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc),
                HistoryRepullStatus.Failed, completedAt: new DateTime(2026, 9, 5, 11, 0, 0, DateTimeKind.Utc)),
            newest,
            Repull(second.Id, member.Id, new DateTime(2026, 9, 7, 10, 0, 0, DateTimeKind.Utc),
                HistoryRepullStatus.Cancelled, completedAt: new DateTime(2026, 9, 7, 11, 0, 0, DateTimeKind.Utc)));

        var latest = await repo.GetLatestByConnectionIdsAsync([first.Id, second.Id]);

        Assert.Equal(2, latest.Count);
        Assert.Equal(newest.Id, latest.Single(r => r.DeviceConnectionId == first.Id).Id);
        Assert.Equal(HistoryRepullStatus.Cancelled, latest.Single(r => r.DeviceConnectionId == second.Id).Status);
    }

    [Fact]
    public async Task GetLatestByConnectionIds_SkipsConnectionsWithNoRepull_AndAnEmptySetCostsNoQuery()
    {
        using var scope = fixture.CreateScope();
        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
        var withOne = await TestDataSeeder.SeedDeviceConnectionAsync(scope, member.Id);
        var withNone = await TestDataSeeder.SeedDeviceConnectionAsync(scope, member.Id);

        var repo = await SaveAsync(
            scope, Repull(withOne.Id, member.Id, new DateTime(2026, 9, 9, 10, 0, 0, DateTimeKind.Utc)));

        var latest = await repo.GetLatestByConnectionIdsAsync([withOne.Id, withNone.Id]);

        Assert.Equal(withOne.Id, Assert.Single(latest).DeviceConnectionId);
        Assert.Empty(await repo.GetLatestByConnectionIdsAsync([]));
    }

    [Fact]
    public async Task GetOpenByConnectionId_FindsPendingAndInProgress_AndNothingTerminal()
    {
        using var scope = fixture.CreateScope();
        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
        var open = await TestDataSeeder.SeedDeviceConnectionAsync(scope, member.Id);
        var settled = await TestDataSeeder.SeedDeviceConnectionAsync(scope, member.Id);

        var repo = await SaveAsync(
            scope,
            Repull(open.Id, member.Id, new DateTime(2026, 9, 9, 10, 0, 0, DateTimeKind.Utc),
                HistoryRepullStatus.InProgress),
            Repull(settled.Id, member.Id, new DateTime(2026, 9, 9, 10, 0, 0, DateTimeKind.Utc),
                HistoryRepullStatus.Completed, completedAt: new DateTime(2026, 9, 9, 11, 0, 0, DateTimeKind.Utc)));

        Assert.NotNull(await repo.GetOpenByConnectionIdAsync(open.Id));
        Assert.Null(await repo.GetOpenByConnectionIdAsync(settled.Id));
    }

    /// <summary>
    /// The rule the service's pre-insert check cannot enforce on its own: two caregivers tapping
    /// at once both pass that check, and the index is what settles it.
    /// </summary>
    [Fact]
    public async Task ASecondOpenRepullForOneConnection_IsRefusedByTheDatabase()
    {
        using var scope = fixture.CreateScope();
        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
        var connection = await TestDataSeeder.SeedDeviceConnectionAsync(scope, member.Id);

        await SaveAsync(
            scope,
            Repull(connection.Id, member.Id, new DateTime(2026, 9, 9, 10, 0, 0, DateTimeKind.Utc),
                HistoryRepullStatus.Pending));

        await Assert.ThrowsAnyAsync<Exception>(() => SaveAsync(
            scope,
            Repull(connection.Id, member.Id, new DateTime(2026, 9, 9, 10, 5, 0, DateTimeKind.Utc),
                HistoryRepullStatus.InProgress)));
    }

    [Fact]
    public async Task GetLastCompletedAt_IgnoresFailedAndCancelledRuns()
    {
        using var scope = fixture.CreateScope();
        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
        var connection = await TestDataSeeder.SeedDeviceConnectionAsync(scope, member.Id);

        var completedAt = new DateTime(2026, 9, 1, 11, 0, 0, DateTimeKind.Utc);
        var repo = await SaveAsync(
            scope,
            Repull(connection.Id, member.Id, new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc),
                HistoryRepullStatus.Completed, completedAt: completedAt),
            // Later, but failed — a re-pull that never delivered must not hold the cooldown.
            Repull(connection.Id, member.Id, new DateTime(2026, 9, 8, 10, 0, 0, DateTimeKind.Utc),
                HistoryRepullStatus.Failed, completedAt: new DateTime(2026, 9, 8, 11, 0, 0, DateTimeKind.Utc)));

        Assert.Equal(completedAt, await repo.GetLastCompletedAtAsync(connection.Id));
    }

    [Fact]
    public async Task GetDue_TakesOpenRequestsOldestFirst_UpToTheLimit()
    {
        using var scope = fixture.CreateScope();
        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);
        var first = await TestDataSeeder.SeedDeviceConnectionAsync(scope, member.Id);
        var second = await TestDataSeeder.SeedDeviceConnectionAsync(scope, member.Id);
        var third = await TestDataSeeder.SeedDeviceConnectionAsync(scope, member.Id);

        var oldest = Repull(first.Id, member.Id, new DateTime(2026, 9, 9, 8, 0, 0, DateTimeKind.Utc));
        var middle = Repull(second.Id, member.Id, new DateTime(2026, 9, 9, 9, 0, 0, DateTimeKind.Utc));
        var repo = await SaveAsync(
            scope,
            middle,
            oldest,
            Repull(third.Id, member.Id, new DateTime(2026, 9, 9, 10, 0, 0, DateTimeKind.Utc)));

        var due = await repo.GetDueAsync(2);

        Assert.Equal([oldest.Id, middle.Id], due.Select(r => r.Id));
    }
}
