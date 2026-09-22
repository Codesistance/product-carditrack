using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using CardiTrack.Infrastructure.Repositories;
using CardiTrack.Infrastructure.Services;
using CardiTrack.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Testcontainers.PostgreSql;

namespace CardiTrack.IntegrationTests.Notifications;

/// <summary>
/// <c>DeviceTokenService.RegisterAsync</c> against a real database, because the whole behaviour is
/// a unique index doing something a substitute repository cannot: <c>IX_PushDeviceTokens_</c>
/// <c>TokenFingerprint</c> is what turned a second install presenting the same push token into a
/// 500 on every registration attempt, and the fix is a delete and an insert whose *order inside
/// one transaction* is the thing that has to hold.
/// </summary>
public class PushTokenClaimTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithCleanUp(true)
        .Build();

    private ServiceProvider _services = null!;

    // Any 32-byte key; these tests never read a token back out.
    private const string EncryptionKey = "yLQb7f0m1Xy0k4V3z8Q9hJ2sN6pR5tW1cE4gU7aD0bM=";

    private const string SharedRawToken = "fcm-token-issued-to-a-cloned-installation";

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var sc = new ServiceCollection();
        sc.AddDbContext<CardiTrackDbContext>(options =>
            options.UseNpgsql(_container.GetConnectionString(),
                b => b.MigrationsAssembly("CardiTrack.Infrastructure")));

        AddRepositories(sc);
        // Not a UnitOfWork constructor parameter, so the reflection loop above never reaches it:
        // the repositories it builds take the write guard themselves (see MemberWriteGuard), and
        // without this every resolve of IUnitOfWork fails on DigestRepository.
        sc.AddScoped<IMemberWriteGuard, MemberWriteGuard>();
        sc.AddScoped<IFamilyWriteGuard, FamilyWriteGuard>();
        // The guard logs, so it needs a logger factory to resolve at all.
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
    /// Every repository in the infrastructure assembly, against the interface it implements.
    /// <see cref="UnitOfWork"/> takes all of them, and listing forty constructor arguments in a
    /// test that uses one of them would only rot.
    /// </summary>
    private static void AddRepositories(IServiceCollection services)
    {
        var implementations = typeof(PushDeviceTokenRepository).Assembly
            .GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false, IsPublic: true, IsGenericTypeDefinition: false }
                           && type.Namespace == typeof(PushDeviceTokenRepository).Namespace);

        foreach (var implementation in implementations)
        {
            foreach (var contract in implementation.GetInterfaces()
                         .Where(i => i.Namespace == typeof(IPushDeviceTokenRepository).Namespace))
            {
                services.AddScoped(contract, implementation);
            }
        }
    }

    private IServiceScope NewScope() => _services.CreateScope();

    private static DeviceTokenService ServiceFor(IServiceScope scope) =>
        new(scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            new AesEncryptionService(EncryptionKey),
            Substitute.For<INotificationGapResolver>());

    private static Task<PushDeviceRegistration> RegisterAsync(
        IServiceScope scope, Guid userId, string deviceId, string rawToken = SharedRawToken) =>
        ServiceFor(scope).RegisterAsync(
            userId, deviceId, DevicePlatform.Android, appVersion: "1.0+1", rawToken,
            OsAuthorizationStatus.Granted, safetyChannelEnabled: true);

    private async Task<List<PushDeviceToken>> RowsAsync()
    {
        using var scope = NewScope();
        return await scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>()
            .Set<PushDeviceToken>().AsNoTracking().ToListAsync();
    }

    [Fact]
    public async Task ASecondInstallPresentingTheSameToken_RegistersInsteadOfViolatingTheIndex()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        using (var scope = NewScope())
            await RegisterAsync(scope, first, "install-a");

        using (var scope = NewScope())
        {
            var registration = await RegisterAsync(scope, second, "install-b");
            Assert.Equal(first, registration.DisplacedUserId);
        }

        // Exactly one row, held by whoever registered last — which is also the only install the
        // push provider will deliver this token to.
        var rows = await RowsAsync();
        Assert.Equal(second, Assert.Single(rows).UserId);
    }

    [Fact]
    public async Task AnAlreadyDisabledRowStillHoldsTheIndexAndIsClaimedToo()
    {
        // The sign-out path disables rather than deletes, and the hard-delete sweep is 30 days
        // behind it. A disabled row keeps its fingerprint, so without claiming it the next
        // caregiver to sign in on this handset would meet the same 500 for a month.
        var signedOut = Guid.NewGuid();
        var signingIn = Guid.NewGuid();

        using (var scope = NewScope())
        {
            await RegisterAsync(scope, signedOut, "shared-handset");
            await ServiceFor(scope).UnregisterAsync(signedOut, "shared-handset");
        }

        using (var scope = NewScope())
        {
            // Same install — the device id names the phone, not the person, and is not cleared
            // at sign-out. Only the caregiver changed.
            var registration = await RegisterAsync(scope, signingIn, "shared-handset");
            Assert.Equal(signedOut, registration.DisplacedUserId);
        }

        var rows = await RowsAsync();
        var row = Assert.Single(rows);
        Assert.Equal(signingIn, row.UserId);
        Assert.Null(row.DisabledDate);
    }

    [Fact]
    public async Task TheSameInstallReRegistering_KeepsOneRowAndDisplacesNobody()
    {
        var user = Guid.NewGuid();

        using (var scope = NewScope())
            await RegisterAsync(scope, user, "install-a");

        using (var scope = NewScope())
        {
            var registration = await RegisterAsync(scope, user, "install-a");
            Assert.Null(registration.DisplacedUserId);
        }

        Assert.Single(await RowsAsync());
    }

    [Fact]
    public async Task TwoInstallsRegisteringTheSameTokenAtOnce_LeaveOneRowAndNoError()
    {
        // Genuinely concurrent, on separate connections. Whether the second one loses the index
        // race and retries, or arrives late enough to find the first row and claim it the
        // ordinary way, is the scheduler's choice — this pins the part that must hold either
        // way. (The retry itself is driven deterministically in DeviceTokenServiceTests.)
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        using var firstScope = NewScope();
        using var secondScope = NewScope();

        await Task.WhenAll(
            RegisterAsync(firstScope, first, "install-a"),
            RegisterAsync(secondScope, second, "install-b"));

        var row = Assert.Single(await RowsAsync());
        Assert.Contains(row.UserId, new[] { first, second });
    }

    [Fact]
    public async Task DistinctTokensAreLeftAlone()
    {
        // The ordinary case, and the one a too-eager claim would break: two caregivers, two
        // phones, two tokens. Nobody displaces anybody.
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        using (var scope = NewScope())
            await RegisterAsync(scope, first, "install-a", rawToken: "fcm-token-for-the-first-phone");

        using (var scope = NewScope())
        {
            var registration = await RegisterAsync(
                scope, second, "install-b", rawToken: "fcm-token-for-the-second-phone");
            Assert.Null(registration.DisplacedUserId);
        }

        Assert.Equal(2, (await RowsAsync()).Count);
    }
}
