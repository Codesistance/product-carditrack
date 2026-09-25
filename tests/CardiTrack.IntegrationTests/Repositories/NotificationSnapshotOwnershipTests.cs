using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using CardiTrack.Infrastructure.Repositories;
using CardiTrack.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace CardiTrack.IntegrationTests.Repositories;

/// <summary>
/// Which members a caller's notification snapshot covers, and whether the caller is the one asked
/// to act on each — against a real Postgres, because both are decided by the queries that load the
/// caregiver links.
/// </summary>
/// <remarks>
/// A per-user snapshot once loaded only that user's own links, so every relative came out as the
/// owner of every member they watched. Visible on the setup checklist the summary carries, and on
/// any nudge first raised by a per-user resolve (un-muting, a time-zone change).
/// </remarks>
public class NotificationSnapshotOwnershipTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc);

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

        // The unit of work's repositories, resolved the way FamilyMembershipTests does, so the
        // summary below runs on the real ones.
        var infrastructure = typeof(UnitOfWork).Assembly;
        foreach (var parameter in typeof(UnitOfWork).GetConstructors().Single().GetParameters())
        {
            if (parameter.ParameterType == typeof(CardiTrackDbContext))
                continue;

            var implementation = infrastructure.GetTypes().Single(t =>
                t.IsClass && !t.IsAbstract && parameter.ParameterType.IsAssignableFrom(t));
            sc.AddScoped(parameter.ParameterType, implementation);
        }
        sc.AddScoped<IMemberWriteGuard, MemberWriteGuard>();
        sc.AddScoped<IFamilyWriteGuard, FamilyWriteGuard>();
        sc.AddLogging();
        sc.AddScoped<IUnitOfWork, UnitOfWork>();
        sc.AddScoped<INotificationSnapshotQueries, NotificationSnapshotQueries>();

        _services = sc.BuildServiceProvider();

        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>().Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        // The container goes even if StartAsync failed before the provider was built.
        try
        {
            if (_services is not null)
                await _services.DisposeAsync();
        }
        finally
        {
            await _container.DisposeAsync();
        }
    }

    [Fact]
    public async Task ARelativesSnapshot_CoversTheMembersTheyCanSee_AndOwnsOnlyTheirOwn()
    {
        var seed = await SeedAsync();

        var contexts = await BuildForUserAsync(seed.Tom);
        var byMember = contexts.Where(c => c.Member is not null).ToDictionary(c => c.Member!.Id);

        // Not Zed (no link), not Hidden (no health-data access), not Gone (link revoked).
        Assert.Equal(
            new[] { seed.Margaret, seed.Funmi }.Order(),
            byMember.Keys.Order());

        Assert.False(byMember[seed.Margaret].IsOwner, "Jane is Margaret's primary caregiver, not Tom.");
        Assert.True(byMember[seed.Funmi].IsOwner, "Tom is Funmi's only caregiver.");
    }

    [Fact]
    public async Task ThePrimaryCaregiversSnapshot_OwnsTheSharedMember()
    {
        var seed = await SeedAsync();

        var contexts = await BuildForUserAsync(seed.Jane);

        Assert.True(Assert.Single(contexts, c => c.Member?.Id == seed.Margaret).IsOwner);
    }

    /// <summary>A caregiver whose account has gone is not anyone's owner — the next one in line is.</summary>
    [Fact]
    public async Task AnInactivePrimary_PassesOwnershipToTheNextCaregiver()
    {
        var seed = await SeedAsync();
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
            var jane = await db.Users.SingleAsync(u => u.Id == seed.Jane);
            jane.IsActive = false;
            await db.SaveChangesAsync();
        }

        var contexts = await BuildForUserAsync(seed.Tom);

        Assert.True(Assert.Single(contexts, c => c.Member?.Id == seed.Margaret).IsOwner);
    }

    /// <summary>The summary's checklist, end to end on the real snapshot: same members, same ownership.</summary>
    [Fact]
    public async Task TheSummarysChecklist_FollowsTheSnapshot()
    {
        var seed = await SeedAsync();

        using var scope = _services.CreateScope();
        var service = new NotificationService(
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            new NoOpGapResolver(),
            scope.ServiceProvider.GetRequiredService<INotificationSnapshotQueries>(),
            new FixedTime(Now));

        var summary = await service.GetSummaryAsync(seed.Tom);

        Assert.Equal(new[] { "Funmi", "Margaret" }, summary.MemberSetup.Select(m => m.CardiMemberFirstName));

        var margaret = summary.MemberSetup.Single(m => m.CardiMemberId == seed.Margaret);
        var funmi = summary.MemberSetup.Single(m => m.CardiMemberId == seed.Funmi);
        Assert.False(margaret.IsOwner);
        Assert.True(funmi.IsOwner);

        // Margaret has an emergency contact and Funmi does not; neither has a device.
        Assert.True(margaret.Steps.Single(s => s.Key == "emergency-contact").Done);
        Assert.False(funmi.Steps.Single(s => s.Key == "emergency-contact").Done);
        Assert.DoesNotContain(funmi.Steps, s => s.Key is "sleep-access" or "irregular-rhythm");
    }

    private async Task<IReadOnlyList<NudgeContext>> BuildForUserAsync(Guid userId)
    {
        using var scope = _services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<INotificationSnapshotQueries>()
            .BuildContextsForUserAsync(userId, Now);
    }

    private sealed record Seed(Guid Jane, Guid Tom, Guid Margaret, Guid Funmi);

    private async Task<Seed> SeedAsync()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        var family = new Organization { Name = "Example family", Type = OrganizationType.Family };
        db.Organizations.Add(family);

        var jane = NewUser(family.Id, "Jane Example", "jane@example.test");
        var tom = NewUser(family.Id, "Tom Example", "tom@example.test");
        db.Users.AddRange(jane, tom);

        var margaret = NewMember(family.Id, "Margaret", emergencyContact: "+440000000000");
        var funmi = NewMember(family.Id, "Funmi", emergencyContact: null);
        var zed = NewMember(family.Id, "Zed", emergencyContact: null);
        var hidden = NewMember(family.Id, "Hidden", emergencyContact: null);
        var gone = NewMember(family.Id, "Gone", emergencyContact: null);
        db.CardiMembers.AddRange(margaret, funmi, zed, hidden, gone);

        db.UserCardiMembers.AddRange(
            // Tom has watched Margaret for longer, but Jane is her primary — primary wins.
            Link(jane.Id, margaret.Id, primary: true, assigned: Now.AddDays(-10)),
            Link(tom.Id, margaret.Id, primary: false, assigned: Now.AddDays(-30)),
            Link(tom.Id, funmi.Id, primary: true, assigned: Now.AddDays(-30)),
            Link(jane.Id, zed.Id, primary: true, assigned: Now.AddDays(-30)),
            Link(tom.Id, hidden.Id, primary: true, assigned: Now.AddDays(-30), canViewHealthData: false),
            Link(tom.Id, gone.Id, primary: true, assigned: Now.AddDays(-30), active: false));

        await db.SaveChangesAsync();
        return new Seed(jane.Id, tom.Id, margaret.Id, funmi.Id);
    }

    private static User NewUser(Guid organizationId, string name, string email) => new()
    {
        OrganizationId = organizationId,
        Name = name,
        Email = email,
        Auth0UserId = $"auth0|{Guid.NewGuid():N}",
        PasswordHash = "n/a",
        Role = UserRole.Member,
        IsActive = true,
        TimeZoneId = "Europe/London"
    };

    private static CardiMember NewMember(Guid organizationId, string firstName, string? emergencyContact) => new()
    {
        OrganizationId = organizationId,
        FirstName = firstName,
        LastName = "Example",
        DateOfBirth = new DateOnly(1943, 4, 2),
        EmergencyContactPhone = emergencyContact,
        IsActive = true
    };

    private static UserCardiMember Link(
        Guid userId, Guid memberId, bool primary, DateTime assigned,
        bool canViewHealthData = true, bool active = true) => new()
    {
        UserId = userId,
        CardiMemberId = memberId,
        RelationshipType = RelationshipType.Parent,
        IsPrimaryCaregiver = primary,
        CanViewHealthData = canViewHealthData,
        AssignedDate = assigned,
        IsActive = active
    };

    private sealed class FixedTime(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private sealed class NoOpGapResolver : INotificationGapResolver
    {
        public Task ResolveForCardiMemberAsync(Guid cardiMemberId, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResolveForUserAsync(Guid userId, CancellationToken ct = default) => Task.CompletedTask;

        public Task WithdrawForCardiMemberAsync(
            Guid cardiMemberId, NotificationResolutionReason reason, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
