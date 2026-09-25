using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using CardiTrack.Infrastructure.Repositories;
using CardiTrack.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace CardiTrack.IntegrationTests.Services;

/// <summary>
/// The rule that a family has exactly one admin, and what that forces everywhere else.
/// </summary>
/// <remarks>
/// Every test here is really about one thing: a family with no admin has nobody able to invite,
/// approve or pay for it, and a family with two has no answer to who the payer is. The handover,
/// the refusals to leave, and the removal of a departing person's grants all fall out of that.
/// </remarks>
public class FamilyMembershipTests : IAsyncLifetime
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
        sc.AddScoped<IFamilyService, FamilyService>();

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

    /// <summary>
    /// Handing the family on is one act, not two. A moment with two admins is a moment where "the
    /// admin pays" names nobody in particular.
    /// </summary>
    [Fact]
    public async Task TransferringAdmin_PromotesAndDemotesInTheSameSave()
    {
        var seed = await SeedAsync();

        using var scope = _services.CreateScope();
        var roster = await Sut(scope).TransferAdminAsync(seed.AdminId, seed.OrganizationId, seed.SiblingId);

        Assert.Equal("admin", roster.Single(r => r.UserId == seed.SiblingId).Role);
        Assert.Equal("member", roster.Single(r => r.UserId == seed.AdminId).Role);

        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        var admins = await db.UserOrganizations
            .Where(m => m.OrganizationId == seed.OrganizationId && m.IsActive && m.Role == UserRole.Admin)
            .CountAsync();
        Assert.Equal(1, admins);
    }

    /// <summary>The roster leads with whoever runs the family, then by how long people have been in it.</summary>
    [Fact]
    public async Task TheRoster_LeadsWithTheAdmin()
    {
        var seed = await SeedAsync();

        using var scope = _services.CreateScope();
        var roster = await Sut(scope).GetMembersAsync(seed.SiblingId, seed.OrganizationId);

        Assert.Equal(seed.AdminId, roster[0].UserId);
        Assert.True(roster.Single(r => r.UserId == seed.SiblingId).IsYou);
        Assert.False(roster[0].IsYou);
    }

    /// <summary>
    /// An admin with other people in the family has to hand it on before going. Letting them
    /// simply leave would strand everyone else in a family nobody can administer.
    /// </summary>
    [Fact]
    public async Task AnAdmin_CannotLeaveWithoutHandingTheFamilyOn()
    {
        var seed = await SeedAsync();

        using var scope = _services.CreateScope();
        var refused = await Assert.ThrowsAsync<FamilyRuleException>(
            () => Sut(scope).LeaveAsync(seed.AdminId, seed.OrganizationId));

        Assert.Equal(FamilyRuleException.AdminMustTransferFirst, refused.Code);
    }

    /// <summary>
    /// Alone in a family they run, leaving is not a family operation — there would be no family
    /// afterwards. They are sent to account deletion rather than quietly deactivated.
    /// </summary>
    [Fact]
    public async Task TheLastPersonInAFamily_IsSentToAccountDeletion()
    {
        var seed = await SeedAsync();
        using (var scope = _services.CreateScope())
        {
            await Sut(scope).RemoveMemberAsync(seed.AdminId, seed.OrganizationId, seed.SiblingId);
        }

        using var check = _services.CreateScope();
        var refused = await Assert.ThrowsAsync<FamilyRuleException>(
            () => Sut(check).LeaveAsync(seed.AdminId, seed.OrganizationId));

        Assert.Equal(FamilyRuleException.UseAccountDeletion, refused.Code);
    }

    /// <summary>
    /// Leaving gives up what you were in the family for. Keeping a live view of its members after
    /// walking out would be a departure in name only.
    /// </summary>
    [Fact]
    public async Task Leaving_TakesTheGrantsOnThatFamilysMembersWithIt()
    {
        var seed = await SeedAsync();

        using (var scope = _services.CreateScope())
        {
            await Sut(scope).LeaveAsync(seed.SiblingId, seed.OrganizationId);
        }

        using var check = _services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        var membership = await db.UserOrganizations.SingleAsync(
            m => m.UserId == seed.SiblingId && m.OrganizationId == seed.OrganizationId);
        Assert.False(membership.IsActive);

        var link = await db.UserCardiMembers.SingleAsync(
            l => l.UserId == seed.SiblingId && l.CardiMemberId == seed.MemberId);
        Assert.False(link.IsActive);

        // The grant they hold in a different family is untouched — leaving one family is not
        // leaving CardiTrack.
        var elsewhere = await db.UserCardiMembers.SingleAsync(
            l => l.UserId == seed.SiblingId && l.CardiMemberId == seed.OtherFamilyMemberId);
        Assert.True(elsewhere.IsActive);
    }

    /// <summary>An admin removing themselves is the leave rule wearing a different hat, and is refused the same way.</summary>
    [Fact]
    public async Task AnAdmin_CannotRemoveThemselves()
    {
        var seed = await SeedAsync();

        using var scope = _services.CreateScope();
        var refused = await Assert.ThrowsAsync<FamilyRuleException>(
            () => Sut(scope).RemoveMemberAsync(seed.AdminId, seed.OrganizationId, seed.AdminId));

        Assert.Equal(FamilyRuleException.CannotDemoteLastAdmin, refused.Code);
    }

    /// <summary>
    /// A member is not an administrator. The refusal reads as "no such family" so somebody outside
    /// cannot map the estate by probing — the same choice the member access checks make.
    /// </summary>
    [Fact]
    public async Task AMember_CannotHandTheFamilyOnOrRemovePeople()
    {
        var seed = await SeedAsync();

        using var scope = _services.CreateScope();
        var sut = Sut(scope);

        var denied = await Assert.ThrowsAsync<KeyNotFoundException>(
            () => sut.TransferAdminAsync(seed.SiblingId, seed.OrganizationId, seed.SiblingId));
        // The literal rather than the constant: what matters is that the refusal a member sees is
        // word-for-word the one a stranger sees, and asserting the shared constant against itself
        // would pass however the message changed.
        Assert.Equal("Family not found", denied.Message);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => sut.RemoveMemberAsync(seed.SiblingId, seed.OrganizationId, seed.AdminId));
    }

    /// <summary>
    /// The families list is per person: it names the one they started as theirs, and shows only the
    /// members they were actually granted in each.
    /// </summary>
    [Fact]
    public async Task TheFamiliesList_SeparatesYourOwnFamilyFromOnesYouJoined()
    {
        var seed = await SeedAsync();

        using var scope = _services.CreateScope();
        var families = await Sut(scope).GetMineAsync(seed.SiblingId);

        var joined = families.Single(f => f.OrganizationId == seed.OrganizationId);
        Assert.Equal("member", joined.Role);
        Assert.False(joined.IsHomeFamily);
        Assert.Equal(["Margaret Okafor"], joined.WatchedMemberNames);

        var own = families.Single(f => f.OrganizationId == seed.OtherOrganizationId);
        Assert.True(own.IsHomeFamily);
        Assert.Equal("admin", own.Role);
    }

    /// <summary>
    /// A family's plan is a ceiling on how many people are in it now, so somebody leaving frees
    /// their place. Counting rows rather than live memberships would charge a family for people
    /// who walked out months ago.
    /// </summary>
    [Fact]
    public async Task ThePersonLimit_CountsWhoIsInTheFamilyNow()
    {
        var seed = await SeedAsync();

        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
            db.Subscriptions.Add(new Subscription
            {
                OrganizationId = seed.OrganizationId,
                Tier = SubscriptionTier.Basic,
                Status = SubscriptionStatus.Active,
                StartDate = DateTime.UtcNow,
                MaxCardiMembers = 1,
                MaxUsers = 2,
            });
            await db.SaveChangesAsync();
        }

        using (var full = _services.CreateScope())
        {
            var refused = await Assert.ThrowsAsync<FamilyRuleException>(
                () => PlanLimits.RequireRoomForAnotherPersonAsync(
                    full.ServiceProvider.GetRequiredService<IUnitOfWork>(), seed.OrganizationId));
            Assert.Equal(FamilyRuleException.MemberLimitReached, refused.Code);
        }

        using (var leaving = _services.CreateScope())
        {
            await Sut(leaving).LeaveAsync(seed.SiblingId, seed.OrganizationId);
        }

        using var after = _services.CreateScope();
        await PlanLimits.RequireRoomForAnotherPersonAsync(
            after.ServiceProvider.GetRequiredService<IUnitOfWork>(), seed.OrganizationId);
    }

    /// <summary>
    /// A family with no subscription is not a family that has run out of room — it is one whose
    /// trial machinery has not run. Refusing it would break onboarding to enforce a limit nobody
    /// is being charged for.
    /// </summary>
    [Fact]
    public async Task NoSubscription_MeansNoCeiling()
    {
        var seed = await SeedAsync();

        using var scope = _services.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await PlanLimits.RequireRoomForAnotherPersonAsync(unitOfWork, seed.OrganizationId);
        await PlanLimits.RequireRoomForAnotherCardiMemberAsync(unitOfWork, seed.OrganizationId);
    }

    // ── The one-admin invariant, enforced rather than promised ─────────────────

    [Fact]
    public async Task TheDatabaseItself_RefusesASecondActiveAdmin()
    {
        var seed = await SeedAsync();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        // Straight past every service, which is the point: IFamilyWriteGuard is what keeps two
        // transfers from racing, but an invariant the roster, the approval queue and "the admin
        // pays" all rest on should not be enforceable only by remembering to take a lock. A future
        // path that forgets has to fail here rather than quietly leave a family with two admins.
        db.UserOrganizations.Add(new UserOrganization
        {
            UserId = Guid.NewGuid(),
            OrganizationId = seed.OrganizationId,
            Role = UserRole.Admin,
            IsActive = true,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task ALapsedAdminMembership_DoesNotBlockTheLiveOne()
    {
        var seed = await SeedAsync();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        // The index is filtered on IsActive, so somebody who was the admin and left still has
        // their row — and it must not stand in the way of whoever runs the family now. Getting
        // this wrong would make a family unadministrable after its first handover.
        db.UserOrganizations.Add(new UserOrganization
        {
            UserId = Guid.NewGuid(),
            OrganizationId = seed.OrganizationId,
            Role = UserRole.Admin,
            IsActive = false,
        });

        await db.SaveChangesAsync();

        Assert.Equal(1, await db.UserOrganizations
            .CountAsync(m => m.OrganizationId == seed.OrganizationId
                             && m.Role == UserRole.Admin && m.IsActive));
    }

    [Fact]
    public async Task HandingTheFamilyOn_PassesThroughNoMomentWithTwoAdmins()
    {
        var seed = await SeedAsync();

        using (var transferring = _services.CreateScope())
            await Sut(transferring).TransferAdminAsync(seed.AdminId, seed.OrganizationId, seed.SiblingId);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        // It succeeding at all is the assertion: the index is checked per statement rather than at
        // commit, and a partial index cannot be deferred, so promoting before demoting would be
        // rejected by the database halfway through the handover.
        var admins = await db.UserOrganizations
            .Where(m => m.OrganizationId == seed.OrganizationId && m.Role == UserRole.Admin && m.IsActive)
            .ToListAsync();

        Assert.Equal(seed.SiblingId, Assert.Single(admins).UserId);
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static IFamilyService Sut(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IFamilyService>();

    private record Seed(
        Guid OrganizationId,
        Guid OtherOrganizationId,
        Guid AdminId,
        Guid SiblingId,
        Guid MemberId,
        Guid OtherFamilyMemberId);

    /// <summary>
    /// Two families. Jane runs the first; Tom is a member of it and runs the second, so every test
    /// that asserts something about one family is also asserting it leaves the other alone.
    /// </summary>
    private async Task<Seed> SeedAsync()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        var okafors = new Organization { Name = "Okafor family", Type = OrganizationType.Family };
        var adeyemis = new Organization { Name = "Adeyemi family", Type = OrganizationType.Family };
        db.Organizations.AddRange(okafors, adeyemis);

        var jane = NewUser(okafors.Id, "Jane Okafor", "jane@okafor.test");
        var tom = NewUser(adeyemis.Id, "Tom Okafor", "tom@okafor.test");
        db.Users.AddRange(jane, tom);

        db.UserOrganizations.AddRange(
            new UserOrganization { UserId = jane.Id, OrganizationId = okafors.Id, Role = UserRole.Admin },
            new UserOrganization { UserId = tom.Id, OrganizationId = okafors.Id, Role = UserRole.Member },
            new UserOrganization { UserId = tom.Id, OrganizationId = adeyemis.Id, Role = UserRole.Admin });

        var margaret = NewMember(okafors.Id, "Margaret Okafor");
        var funmi = NewMember(adeyemis.Id, "Funmi Adeyemi");
        db.CardiMembers.AddRange(margaret, funmi);

        db.UserCardiMembers.AddRange(
            new UserCardiMember
            {
                UserId = jane.Id,
                CardiMemberId = margaret.Id,
                RelationshipType = RelationshipType.Parent,
                IsPrimaryCaregiver = true,
            },
            new UserCardiMember
            {
                UserId = tom.Id,
                CardiMemberId = margaret.Id,
                RelationshipType = RelationshipType.Parent,
            },
            new UserCardiMember
            {
                UserId = tom.Id,
                CardiMemberId = funmi.Id,
                RelationshipType = RelationshipType.Parent,
                IsPrimaryCaregiver = true,
            });

        await db.SaveChangesAsync();
        return new Seed(okafors.Id, adeyemis.Id, jane.Id, tom.Id, margaret.Id, funmi.Id);
    }

    private static User NewUser(Guid? organizationId, string name, string email) => new()
    {
        OrganizationId = organizationId,
        Name = name,
        Email = email,
        Auth0UserId = $"auth0|{Guid.NewGuid():N}",
        PasswordHash = "n/a",
        Role = UserRole.Member,
        IsActive = true,
    };

    private static CardiMember NewMember(Guid organizationId, string name) => new()
    {
        OrganizationId = organizationId,
        Name = name,
        DateOfBirth = new DateOnly(1943, 4, 2),
        IsActive = true,
    };
}
