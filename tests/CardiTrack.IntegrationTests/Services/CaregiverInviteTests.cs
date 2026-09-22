using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using CardiTrack.Infrastructure.Repositories;
using CardiTrack.Infrastructure.Services;
using CardiTrack.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Testcontainers.PostgreSql;

namespace CardiTrack.IntegrationTests.Services;

/// <summary>
/// Whether an invitation grants exactly what it was written to grant, to exactly one person, and
/// only while the authority behind it still stands.
/// </summary>
/// <remarks>
/// Against a real Postgres because the interesting parts are database-shaped: the conditional
/// resolve that decides a race, the unique index that keeps a re-join to one membership row, and
/// the ordering that claims the invitation before writing any grant.
/// </remarks>
public class CaregiverInviteTests : IAsyncLifetime
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

        // UnitOfWork takes every repository, and they all construct from the DbContext alone, so
        // each constructor parameter's interface is registered against the one Infrastructure
        // class implementing it — the same trick CardiMemberCreationTransactionTests uses, and the
        // reason this test exercises the real UnitOfWork rather than a stub that would have to be
        // widened every time the interface grows.
        var infrastructure = typeof(UnitOfWork).Assembly;
        foreach (var parameter in typeof(UnitOfWork).GetConstructors().Single().GetParameters())
        {
            if (parameter.ParameterType == typeof(CardiTrackDbContext))
                continue;

            var implementation = infrastructure.GetTypes().Single(t =>
                t.IsClass && !t.IsAbstract && parameter.ParameterType.IsAssignableFrom(t));
            sc.AddScoped(parameter.ParameterType, implementation);
        }
        // Not a UnitOfWork constructor parameter, so the loop never reaches it, and without it
        // every resolve of IUnitOfWork fails on DigestRepository.
        sc.AddScoped<IMemberWriteGuard, MemberWriteGuard>();
        sc.AddScoped<IFamilyWriteGuard, FamilyWriteGuard>();
        sc.AddLogging();
        sc.AddScoped<IUnitOfWork, UnitOfWork>();

        _services = sc.BuildServiceProvider();

        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>().Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _container.DisposeAsync();
    }

    /// <summary>
    /// The whole point: an admin's invitation becomes a membership and a grant carrying the flags
    /// the admin chose, not the defaults.
    /// </summary>
    [Fact]
    public async Task Redeeming_CreatesTheMembershipAndTheGrant_WithTheFlagsTheAdminChose()
    {
        var seed = await SeedAsync();
        var url = await CreateInviteAsync(seed, new CreateCaregiverInviteRequest
        {
            Role = "member",
            CanViewHealthData = true,
            ReceiveAlerts = false,
        });

        var redemption = await RedeemAsync(TokenFrom(url), seed.SiblingId);

        Assert.Equal(seed.MemberId, redemption.CardiMemberId);
        Assert.Equal("member", redemption.Role);
        Assert.False(redemption.AlreadyHadAccess);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        var membership = await db.UserOrganizations.SingleAsync(
            m => m.UserId == seed.SiblingId && m.OrganizationId == seed.OrganizationId);
        Assert.Equal(UserRole.Member, membership.Role);
        Assert.True(membership.IsActive);

        var link = await db.UserCardiMembers.SingleAsync(
            l => l.UserId == seed.SiblingId && l.CardiMemberId == seed.MemberId);
        Assert.True(link.CanViewHealthData);
        Assert.False(link.ReceiveAlerts);
        Assert.False(link.IsPrimaryCaregiver);

        var invite = await db.CaregiverInvites.SingleAsync();
        Assert.Equal(CaregiverInviteStatus.Accepted, invite.Status);
        Assert.Equal(seed.SiblingId, invite.AcceptedByUserId);
    }

    /// <summary>
    /// An invitation must not outlive the authority that issued it. Demoting the issuer has to
    /// stop a link they already sent, or revoking somebody's admin rights would be cosmetic for as
    /// long as an old message sat in an inbox.
    /// </summary>
    [Fact]
    public async Task Redeeming_IsRefused_WhenTheIssuerIsNoLongerAnAdmin()
    {
        var seed = await SeedAsync();
        var url = await CreateInviteAsync(seed, new CreateCaregiverInviteRequest { Role = "member" });

        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
            var membership = await db.UserOrganizations.SingleAsync(m => m.UserId == seed.AdminId);
            membership.Role = UserRole.Member;
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => RedeemAsync(TokenFrom(url), seed.SiblingId));

        using var check = _services.CreateScope();
        var checkDb = check.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        Assert.Equal(0, await checkDb.UserCardiMembers.CountAsync(l => l.UserId == seed.SiblingId));
        // The invitation is left live rather than spent: the authority may come back, and a
        // refusal is not the invitee's doing.
        Assert.Equal(CaregiverInviteStatus.Pending, (await checkDb.CaregiverInvites.SingleAsync()).Status);
    }

    /// <summary>A revoked invitation is dead, and revoking is what an admin does to take one back.</summary>
    [Fact]
    public async Task Redeeming_IsRefused_AfterTheAdminRevokedIt()
    {
        var seed = await SeedAsync();
        var url = await CreateInviteAsync(seed, new CreateCaregiverInviteRequest { Role = "member" });

        using (var scope = _services.CreateScope())
        {
            var invites = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>().CaregiverInvites;
            var inviteId = (await invites.SingleAsync()).Id;
            await Sut(scope).RevokeAsync(seed.AdminId, seed.MemberId, inviteId);
        }

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => RedeemAsync(TokenFrom(url), seed.SiblingId));
    }

    /// <summary>
    /// One invitation, one redemption. The second person to tap Accept is told the invitation is
    /// gone rather than quietly given a grant the admin issued once.
    /// </summary>
    [Fact]
    public async Task AnInvitation_CanOnlyBeRedeemedOnce()
    {
        var seed = await SeedAsync();
        var url = await CreateInviteAsync(seed, new CreateCaregiverInviteRequest { Role = "member" });
        var token = TokenFrom(url);

        await RedeemAsync(token, seed.SiblingId);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => RedeemAsync(token, seed.OutsiderId));

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        Assert.Equal(0, await db.UserCardiMembers.CountAsync(l => l.UserId == seed.OutsiderId));
    }

    /// <summary>
    /// Somebody who already watches this member redeeming a second invitation keeps what they had.
    /// The invitation still resolves — it has been used — but a narrower or wider offer must not
    /// silently rewrite an existing grant.
    /// </summary>
    [Fact]
    public async Task Redeeming_DoesNotWidenAnExistingGrant()
    {
        var seed = await SeedAsync();
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
            db.UserCardiMembers.Add(new UserCardiMember
            {
                UserId = seed.SiblingId,
                CardiMemberId = seed.MemberId,
                RelationshipType = RelationshipType.Child,
                CanViewHealthData = true,
                ReceiveAlerts = false,
            });
            await db.SaveChangesAsync();
        }

        var url = await CreateInviteAsync(seed, new CreateCaregiverInviteRequest
        {
            Role = "member",
            CanViewHealthData = true,
            ReceiveAlerts = true,
        });

        var redemption = await RedeemAsync(TokenFrom(url), seed.SiblingId);

        Assert.True(redemption.AlreadyHadAccess);
        Assert.False(redemption.ReceiveAlerts);

        using var check = _services.CreateScope();
        var checkDb = check.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        var link = await checkDb.UserCardiMembers.SingleAsync(l => l.UserId == seed.SiblingId);
        Assert.False(link.ReceiveAlerts);
        Assert.Equal(RelationshipType.Child, link.RelationshipType);
    }

    /// <summary>
    /// Only an admin of the member's own family may invite. A caregiver who can see the member is
    /// not enough, and the refusal reads the same as a member that does not exist so an outsider
    /// cannot map the estate by probing.
    /// </summary>
    [Fact]
    public async Task OnlyAnAdminOfTheMembersFamilyCanInvite()
    {
        var seed = await SeedAsync();

        using var scope = _services.CreateScope();
        var sut = Sut(scope);

        var denied = await Assert.ThrowsAsync<KeyNotFoundException>(() => sut.CreateAsync(
            seed.OutsiderId, seed.MemberId, new CreateCaregiverInviteRequest { Role = "member" }, BaseUrl));
        Assert.Equal("CardiMember not found", denied.Message);
    }

    /// <summary>
    /// The landing page says two first names and a deadline. Nothing here should ever grow a
    /// surname, an email or a reading, so the test pins the shape rather than only the happy path.
    /// </summary>
    [Fact]
    public async Task TheLandingView_SaysOnlyFirstNamesAndADeadline_AndMarksTheInviteOpened()
    {
        var seed = await SeedAsync();
        var url = await CreateInviteAsync(seed, new CreateCaregiverInviteRequest { Role = "member" });

        using var scope = _services.CreateScope();
        var view = await Sut(scope).ViewAsync(TokenFrom(url));

        Assert.NotNull(view);
        Assert.Equal("Margaret", view!.MemberFirstName);
        Assert.Equal("Jane", view.InviterFirstName);

        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        var invite = await db.CaregiverInvites.AsNoTracking().SingleAsync();
        Assert.Equal(CaregiverInviteStatus.Opened, invite.Status);
        Assert.NotNull(invite.OpenedAt);
    }

    /// <summary>An unknown token is indistinguishable from an expired or spent one.</summary>
    [Fact]
    public async Task AnUnknownToken_LooksExactlyLikeASpentOne()
    {
        await SeedAsync();

        using var scope = _services.CreateScope();
        Assert.Null(await Sut(scope).ViewAsync("not-a-real-token"));
        Assert.Null(await Sut(scope).ViewAsync(string.Empty));
    }

    /// <summary>
    /// Someone who left a family and is invited back gets their old membership row reactivated,
    /// not a second one — the unique index would refuse the insert, and one row is also the honest
    /// record of a person's history with a family.
    /// </summary>
    [Fact]
    public async Task RejoiningAFamily_ReactivatesTheMembershipRatherThanAddingASecond()
    {
        var seed = await SeedAsync();
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
            db.UserOrganizations.Add(new UserOrganization
            {
                UserId = seed.SiblingId,
                OrganizationId = seed.OrganizationId,
                Role = UserRole.Member,
                IsActive = false,
            });
            await db.SaveChangesAsync();
        }

        var url = await CreateInviteAsync(seed, new CreateCaregiverInviteRequest { Role = "member" });
        await RedeemAsync(TokenFrom(url), seed.SiblingId);

        using var check = _services.CreateScope();
        var checkDb = check.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        var membership = await checkDb.UserOrganizations.SingleAsync(
            m => m.UserId == seed.SiblingId && m.OrganizationId == seed.OrganizationId);
        Assert.True(membership.IsActive);
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private const string BaseUrl = "https://api.test.carditrack.com";

    /// <summary>
    /// Audit is substituted, as it is in every other test of a service that writes one: the real
    /// repository writes on its own DbContext so an entry survives a rolled-back transaction, and
    /// none of the behaviour under test here turns on that. The service swallows audit failures by
    /// design, so a substitute cannot mask a real one either.
    /// </summary>
    private static ICaregiverInviteService Sut(IServiceScope scope) =>
        new CaregiverInviteService(
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            Substitute.For<IAuditLogRepository>(),
            Options.Create(new CaregiverInviteOptions { PublicBaseUrl = BaseUrl, LifetimeDays = 7 }),
            scope.ServiceProvider.GetRequiredService<ILogger<CaregiverInviteService>>(),
            // The real guard, on the same DbContext: redemption now runs inside a transaction that
            // holds the family, and substituting it away would leave the transaction untested.
            new CardiTrack.Infrastructure.Services.FamilyWriteGuard(
                scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>()));

    private async Task<string> CreateInviteAsync(Seed seed, CreateCaregiverInviteRequest request)
    {
        using var scope = _services.CreateScope();
        var response = await Sut(scope).CreateAsync(seed.AdminId, seed.MemberId, request, BaseUrl);
        Assert.NotNull(response.Url);
        return response.Url!;
    }

    private async Task<Application.DTOs.Responses.CaregiverInviteRedemption> RedeemAsync(
        string token, Guid userId)
    {
        using var scope = _services.CreateScope();
        return await Sut(scope).RedeemAsync(token, userId);
    }

    private static string TokenFrom(string url) => url[(url.IndexOf("t=", StringComparison.Ordinal) + 2)..];

    private record Seed(Guid OrganizationId, Guid AdminId, Guid SiblingId, Guid OutsiderId, Guid MemberId);

    private async Task<Seed> SeedAsync()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        var organization = new Organization { Name = "Okafor family", Type = OrganizationType.Family };
        var elsewhere = new Organization { Name = "Adeyemi family", Type = OrganizationType.Family };
        db.Organizations.AddRange(organization, elsewhere);

        var admin = NewUser(organization.Id, "Jane Okafor", "jane@okafor.test");
        var sibling = NewUser(null, "Tom Okafor", "tom@okafor.test");
        var outsider = NewUser(elsewhere.Id, "Bisi Adeyemi", "bisi@adeyemi.test");
        db.Users.AddRange(admin, sibling, outsider);

        db.UserOrganizations.AddRange(
            new UserOrganization { UserId = admin.Id, OrganizationId = organization.Id, Role = UserRole.Admin },
            new UserOrganization { UserId = outsider.Id, OrganizationId = elsewhere.Id, Role = UserRole.Admin });

        var member = new CardiMember
        {
            OrganizationId = organization.Id,
            Name = "Margaret Okafor",
            DateOfBirth = new DateOnly(1943, 4, 2),
            IsActive = true,
        };
        db.CardiMembers.Add(member);

        db.UserCardiMembers.Add(new UserCardiMember
        {
            UserId = admin.Id,
            CardiMemberId = member.Id,
            RelationshipType = RelationshipType.Parent,
            IsPrimaryCaregiver = true,
        });

        await db.SaveChangesAsync();
        return new Seed(organization.Id, admin.Id, sibling.Id, outsider.Id, member.Id);
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
}
