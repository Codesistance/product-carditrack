using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using CardiTrack.Infrastructure.Repositories;
using CardiTrack.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace CardiTrack.IntegrationTests.Services;

/// <summary>
/// Signing up to join a family somebody else runs, and what happens later if you start your own.
/// </summary>
/// <remarks>
/// The promise being tested is narrow and worth stating: a guest's thirty-day trial starts when
/// they first add somebody to watch, not when they sign up. Starting it at signup would burn it
/// while they waited on an approval they might never get, and would make "only the admin pays" a
/// sentence with a hole in it.
/// </remarks>
public class GuestOnboardingTests : IAsyncLifetime
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
        sc.AddScoped<ISubscriptionService, SubscriptionService>();
        sc.AddScoped<IOnboardingService, OnboardingService>();
        sc.AddScoped<IGuestFamilyProvisioner, GuestFamilyProvisioner>();

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
    /// Signing up without asking for a family leaves an account and nothing else: no family, no
    /// membership, and above all no trial ticking down.
    /// </summary>
    [Fact]
    public async Task SigningUpAsAGuest_CreatesNoFamilyAndNoTrial()
    {
        using var scope = _services.CreateScope();
        var response = await Onboarding(scope).SetupAsync(GuestRequest(), "auth0|tom", emailVerified: true);

        Assert.Null(response.User.OrganizationId);

        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        Assert.Equal(0, await db.Organizations.CountAsync());
        Assert.Equal(0, await db.Subscriptions.CountAsync());
        Assert.Equal(0, await db.UserOrganizations.CountAsync());
        Assert.Equal(1, await db.Users.CountAsync());
    }

    /// <summary>Asking for a family still does what it always did, admin membership and all.</summary>
    [Fact]
    public async Task SigningUpWithAFamily_StillCreatesEverythingAtOnce()
    {
        using var scope = _services.CreateScope();
        var response = await Onboarding(scope).SetupAsync(FamilyRequest(), "auth0|jane", emailVerified: true);

        Assert.NotNull(response.User.OrganizationId);

        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        var organization = await db.Organizations.SingleAsync();
        Assert.Equal("Okafor family", organization.Name);
        Assert.Equal(8, organization.FamilyId.Length);
        Assert.Equal(1, await db.Subscriptions.CountAsync(s => s.OrganizationId == organization.Id));

        var membership = await db.UserOrganizations.SingleAsync();
        Assert.Equal(UserRole.Admin, membership.Role);
    }

    /// <summary>
    /// The trial starts here, on their first member — the promise this whole path exists to keep.
    /// </summary>
    [Fact]
    public async Task AGuestsFirstMember_CreatesTheirFamilyAndStartsTheirTrial()
    {
        Guid userId;
        using (var signup = _services.CreateScope())
        {
            var response = await Onboarding(signup).SetupAsync(GuestRequest(), "auth0|tom", emailVerified: true);
            userId = response.User.Id;
        }

        Guid organizationId;
        using (var adding = _services.CreateScope())
        {
            organizationId = await Provisioner(adding).ResolveHomeOrganizationAsync(userId);
        }

        using var check = _services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        var user = await db.Users.SingleAsync(u => u.Id == userId);
        Assert.Equal(organizationId, user.OrganizationId);

        var organization = await db.Organizations.SingleAsync();
        Assert.Equal("The Okafors", organization.Name);
        Assert.Equal(8, organization.FamilyId.Length);

        var subscription = await db.Subscriptions.SingleAsync();
        Assert.Equal(SubscriptionStatus.Trial, subscription.Status);
        Assert.NotNull(subscription.TrialEndDate);

        var membership = await db.UserOrganizations.SingleAsync(m => m.UserId == userId);
        Assert.Equal(UserRole.Admin, membership.Role);
    }

    /// <summary>
    /// Resolving twice must not mint a second family. A retried creation is the ordinary case, not
    /// an exotic one, and two families would leave the second holding a trial nobody uses.
    /// </summary>
    [Fact]
    public async Task ResolvingTwice_KeepsTheOneFamily()
    {
        Guid userId;
        using (var signup = _services.CreateScope())
        {
            userId = (await Onboarding(signup).SetupAsync(GuestRequest(), "auth0|tom", emailVerified: true)).User.Id;
        }

        Guid first, second;
        using (var one = _services.CreateScope())
            first = await Provisioner(one).ResolveHomeOrganizationAsync(userId);
        using (var two = _services.CreateScope())
            second = await Provisioner(two).ResolveHomeOrganizationAsync(userId);

        Assert.Equal(first, second);

        using var check = _services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        Assert.Equal(1, await db.Organizations.CountAsync());
        Assert.Equal(1, await db.Subscriptions.CountAsync());
    }

    /// <summary>
    /// Somebody who already has a family gets it back untouched — no second family, no second
    /// trial, and the name they chose is left alone.
    /// </summary>
    [Fact]
    public async Task SomebodyWithAFamily_GetsItBackUnchanged()
    {
        Guid userId, organizationId;
        using (var signup = _services.CreateScope())
        {
            var response = await Onboarding(signup).SetupAsync(FamilyRequest(), "auth0|jane", emailVerified: true);
            (userId, organizationId) = (response.User.Id, response.Organization.Id);
        }

        using var scope = _services.CreateScope();
        var resolved = await Provisioner(scope).ResolveHomeOrganizationAsync(userId);

        Assert.Equal(organizationId, resolved);

        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        Assert.Equal("Okafor family", (await db.Organizations.SingleAsync()).Name);
        Assert.Equal(1, await db.Subscriptions.CountAsync());
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static IOnboardingService Onboarding(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IOnboardingService>();

    private static IGuestFamilyProvisioner Provisioner(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IGuestFamilyProvisioner>();

    private static OnboardingSetupRequest GuestRequest() => new()
    {
        Organization = null,
        User = User("Tom Okafor", "tom@okafor.test"),
    };

    private static OnboardingSetupRequest FamilyRequest() => new()
    {
        Organization = new CreateOrganizationRequest
        {
            Name = "Okafor family",
            Type = OrganizationType.Family,
        },
        User = User("Jane Okafor", "jane@okafor.test"),
    };

    private static OnboardingSetupUserRequest User(string name, string email) => new()
    {
        Email = email,
        Name = name,
        TimeZoneId = "Europe/London",
    };
}
