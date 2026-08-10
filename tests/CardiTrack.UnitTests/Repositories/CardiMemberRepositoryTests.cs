using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.UnitTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CardiTrack.UnitTests.Repositories;

[Collection("DatabaseCollection")]
public class CardiMemberRepositoryTests(TestDatabaseFixture fixture)
{
    private static ActivityLog BuildLog(Guid cardiMemberId, Guid deviceConnectionId, DateOnly date) =>
        new()
        {
            CardiMemberId = cardiMemberId,
            DeviceConnectionId = deviceConnectionId,
            DataSource = DeviceType.Fitbit,
            Date = date,
            Steps = 8000
        };

    private static async Task SeedActivityLogAsync(IServiceScope scope, Guid memberId, DateOnly date)
    {
        var logRepo = scope.ServiceProvider.GetRequiredService<IActivityLogRepository>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var connection = await TestDataSeeder.SeedDeviceConnectionAsync(scope, memberId);
        await logRepo.UpsertAsync(BuildLog(memberId, connection.Id, date));
        await uow.SaveChangesAsync();
    }

    // ── GetActiveIdsWithActivitySinceAsync ───────────────────────────────────────

    [Fact]
    public async Task GetActiveIdsWithActivitySinceAsync_ReturnsMember_WithActivityOnOrAfterSince()
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICardiMemberRepository>();

        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);

        var since = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-7));
        await SeedActivityLogAsync(scope, member.Id, since);

        var ids = await repo.GetActiveIdsWithActivitySinceAsync(since);

        Assert.Contains(member.Id, ids);
    }

    [Fact]
    public async Task GetActiveIdsWithActivitySinceAsync_ExcludesMember_WithOnlyOlderActivity()
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICardiMemberRepository>();

        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);

        var since = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-7));
        await SeedActivityLogAsync(scope, member.Id, since.AddDays(-1));

        var ids = await repo.GetActiveIdsWithActivitySinceAsync(since);

        Assert.DoesNotContain(member.Id, ids);
    }

    [Fact]
    public async Task GetActiveIdsWithActivitySinceAsync_ExcludesInactiveMember_WithRecentActivity()
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICardiMemberRepository>();

        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id, isActive: false);

        var since = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-7));
        await SeedActivityLogAsync(scope, member.Id, since);

        var ids = await repo.GetActiveIdsWithActivitySinceAsync(since);

        Assert.DoesNotContain(member.Id, ids);
    }

    [Fact]
    public async Task GetActiveIdsWithActivitySinceAsync_ExcludesMember_WithNoActivityAtAll()
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICardiMemberRepository>();

        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);

        var ids = await repo.GetActiveIdsWithActivitySinceAsync(
            DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-7)));

        Assert.DoesNotContain(member.Id, ids);
    }

    // ── GetWithRelationshipsAsync ────────────────────────────────────────────────

    [Fact]
    public async Task GetWithRelationshipsAsync_LoadsUserCardiMembers()
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICardiMemberRepository>();
        var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var linkRepo = scope.ServiceProvider.GetRequiredService<IUserCardiMemberRepository>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var org = await TestDataSeeder.SeedOrganizationAsync(scope);
        var member = await TestDataSeeder.SeedCardiMemberAsync(scope, org.Id);

        var user = new User
        {
            OrganizationId = org.Id,
            Auth0UserId = $"auth0|{Guid.NewGuid():N}",
            Email = $"caregiver-{Guid.NewGuid():N}@example.com",
            Name = "Test Caregiver",
            Role = UserRole.Member
        };
        await userRepo.AddAsync(user);

        var link = new UserCardiMember
        {
            UserId = user.Id,
            CardiMemberId = member.Id,
            RelationshipType = RelationshipType.Child,
            IsPrimaryCaregiver = true
        };
        await linkRepo.AddAsync(link);
        await uow.SaveChangesAsync();

        var loaded = await repo.GetWithRelationshipsAsync(member.Id);

        Assert.NotNull(loaded);
        var loadedLink = Assert.Single(loaded.UserCardiMembers);
        Assert.Equal(user.Id, loadedLink.UserId);
        Assert.True(loadedLink.IsPrimaryCaregiver);
    }

    [Fact]
    public async Task GetWithRelationshipsAsync_ReturnsNull_WhenMemberDoesNotExist()
    {
        using var scope = fixture.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICardiMemberRepository>();

        var loaded = await repo.GetWithRelationshipsAsync(Guid.NewGuid());

        Assert.Null(loaded);
    }
}
