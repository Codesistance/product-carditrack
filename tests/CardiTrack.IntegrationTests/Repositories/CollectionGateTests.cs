using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using CardiTrack.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace CardiTrack.IntegrationTests.Repositories;

/// <summary>
/// The predicate that decides whether a pending deletion has stopped collection for a member.
/// </summary>
/// <remarks>
/// <para>
/// It exists in two places — spelled inline at <c>DeviceConnectionRepository</c>'s four
/// sync-scheduling sites, and as
/// <c>IUserCardiMemberRepository.IsLeftUnwatchedByPendingDeletionAsync</c> for the paths that
/// reach the sync service without going through the scheduler. <strong>They have to agree.</strong>
/// A member that routine sync collects for but that inactivity detection skips is a member whose
/// readings pile up while nobody is told anything about them.
/// </para>
/// <para>
/// Against a real Postgres, because the whole thing is one SQL predicate: mocked at the repository
/// boundary — which is how the inactivity tests take it — these cases would pass whatever the
/// query said.
/// </para>
/// </remarks>
public class CollectionGateTests : IAsyncLifetime
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
        sc.AddLogging();
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

    /// <summary>The ordinary case: somebody is watching and staying, so collection continues.</summary>
    [Fact]
    public async Task AMemberWhoseOnlyWatcherIsStaying_IsStillCollectedFor()
    {
        var memberId = await SeedMemberAsync((Deleting: false, Active: true));

        Assert.False(await IsLeftUnwatchedAsync(memberId));
    }

    /// <summary>The case the gate is for.</summary>
    [Fact]
    public async Task AMemberWhoseOnlyWatcherIsLeaving_IsNotCollectedFor()
    {
        var memberId = await SeedMemberAsync((Deleting: true, Active: true));

        Assert.True(await IsLeftUnwatchedAsync(memberId));
    }

    /// <summary>
    /// One relative leaving does not stop monitoring for a member another relative still watches —
    /// the same asymmetry the erasure cascade's release rule turns on.
    /// </summary>
    [Fact]
    public async Task AMemberOneOfWhoseTwoWatchersIsLeaving_IsStillCollectedFor()
    {
        var memberId = await SeedMemberAsync(
            (Deleting: true, Active: true), (Deleting: false, Active: true));

        Assert.False(await IsLeftUnwatchedAsync(memberId));
    }

    /// <summary>
    /// An inactive link is somebody who has already removed this member, so it does not count as
    /// watching — and cannot be what keeps collection alive for a member everyone else is leaving.
    /// </summary>
    [Fact]
    public async Task AStayingCaregiverWhoAlreadyRemovedTheMember_DoesNotKeepCollectionAlive()
    {
        var memberId = await SeedMemberAsync(
            (Deleting: true, Active: true), (Deleting: false, Active: false));

        Assert.True(await IsLeftUnwatchedAsync(memberId));
    }

    /// <summary>
    /// The half that is easy to get wrong, and the reason this file exists: a member with no
    /// active link at all is <em>still collected for</em>. No pending deletion abandoned them — an
    /// ordinary removal did — they may be re-linked, and the readings in the gap are theirs. The
    /// scheduler says the same (`!Any(active) || …`), and the two must not disagree.
    /// </summary>
    [Fact]
    public async Task AMemberNobodyIsWatching_IsStillCollectedFor()
    {
        var memberId = await SeedMemberAsync((Deleting: false, Active: false));

        Assert.False(await IsLeftUnwatchedAsync(memberId));
    }

    /// <summary>And a member with no link row at all, which is the same answer for the same reason.</summary>
    [Fact]
    public async Task AMemberWithNoLinkAtAll_IsStillCollectedFor()
    {
        var memberId = await SeedMemberAsync();

        Assert.False(await IsLeftUnwatchedAsync(memberId));
    }

    private async Task<bool> IsLeftUnwatchedAsync(Guid memberId)
    {
        using var scope = _services.CreateScope();
        var repository = new UserCardiMemberRepository(
            scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>());
        return await repository.IsLeftUnwatchedByPendingDeletionAsync(memberId);
    }

    /// <summary>One member and a caregiver per link described.</summary>
    private async Task<Guid> SeedMemberAsync(params (bool Deleting, bool Active)[] watchers)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        var organization = new Organization { Name = "Doe family", Type = OrganizationType.Family };
        db.Organizations.Add(organization);

        var member = new CardiMember
        {
            OrganizationId = organization.Id,
            FirstName = "Margaret",
            LastName = "Doe",
            DateOfBirth = new DateOnly(1948, 4, 2),
            Gender = Gender.Female,
            IsActive = true,
        };
        db.CardiMembers.Add(member);

        var users = watchers.Select(w => new User
        {
            OrganizationId = organization.Id,
            Email = $"caregiver-{Guid.NewGuid():N}@example.com",
            Name = "Jane Doe",
            DeletionRequestedAtUtc = w.Deleting ? DateTime.UtcNow.AddDays(-5) : null,
        }).ToList();
        db.Users.AddRange(users);
        await db.SaveChangesAsync();

        for (var i = 0; i < watchers.Length; i++)
        {
            db.UserCardiMembers.Add(new UserCardiMember
            {
                UserId = users[i].Id,
                CardiMemberId = member.Id,
                RelationshipType = RelationshipType.Parent,
                IsPrimaryCaregiver = i == 0,
                CanViewHealthData = true,
                ReceiveAlerts = true,
                IsActive = watchers[i].Active,
            });
        }

        await db.SaveChangesAsync();
        return member.Id;
    }
}
