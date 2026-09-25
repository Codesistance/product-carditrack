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
using NSubstitute;
using Testcontainers.PostgreSql;

namespace CardiTrack.IntegrationTests.Services;

/// <summary>
/// The deletion request's state machine: ask, ask again, cancel, and cancel too late.
/// </summary>
/// <remarks>
/// Against a real database rather than a substituted repository, because every interesting claim
/// here is about a conditional <c>UPDATE</c> — the <c>WHERE</c> is what makes the first request's
/// timestamp the one that counts, and a substitute would happily agree with whatever the service
/// asked for.
/// </remarks>
public class AccountDeletionStateTests : IAsyncLifetime
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
    public async Task AFreshAccount_IsNotAwaitingDeletion()
    {
        var auth0Id = await SeedUserAsync();

        var status = await WithService(s => s.GetDeletionStatusAsync(auth0Id));

        Assert.NotNull(status);
        Assert.False(status!.DeletionRequested);
        Assert.Null(status.ScheduledForUtc);
        Assert.False(status.CanCancel);
    }

    [Fact]
    public async Task Requesting_SchedulesErasureThirtyDaysOut()
    {
        var auth0Id = await SeedUserAsync();

        var status = await WithService(s => s.RequestDeletionAsync(auth0Id));

        Assert.NotNull(status);
        Assert.True(status!.DeletionRequested);
        Assert.True(status.CanCancel);
        Assert.Equal(
            status.RequestedAtUtc!.Value.AddDays(30), status.ScheduledForUtc!.Value, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// The one that matters: a second request must not push the erasure further away. A restarted
    /// clock keeps the data longer than the caregiver was told it would be kept.
    /// </summary>
    [Fact]
    public async Task RequestingTwice_KeepsTheFirstDueDate()
    {
        var auth0Id = await SeedUserAsync();

        var first = await WithService(s => s.RequestDeletionAsync(auth0Id));
        await Task.Delay(50);
        var second = await WithService(s => s.RequestDeletionAsync(auth0Id));

        Assert.Equal(first!.RequestedAtUtc, second!.RequestedAtUtc);
        Assert.Equal(first.ScheduledForUtc, second.ScheduledForUtc);
    }

    [Fact]
    public async Task Cancelling_ClearsTheRequest()
    {
        var auth0Id = await SeedUserAsync();
        await WithService(s => s.RequestDeletionAsync(auth0Id));

        var status = await WithService(s => s.CancelDeletionAsync(auth0Id));

        Assert.False(status!.DeletionRequested);
        Assert.Null(status.RequestedAtUtc);

        var reread = await WithService(s => s.GetDeletionStatusAsync(auth0Id));
        Assert.False(reread!.DeletionRequested);
    }

    /// <summary>
    /// Cancelling when nothing was requested is not an error — the caller wanted an account that
    /// is not being deleted, and that is what they have.
    /// </summary>
    [Fact]
    public async Task Cancelling_WithNothingPending_Succeeds()
    {
        var auth0Id = await SeedUserAsync();

        var status = await WithService(s => s.CancelDeletionAsync(auth0Id));

        Assert.False(status!.DeletionRequested);
    }

    /// <summary>
    /// Past the window the answer is no. Letting a cancellation land on day 31 would either revive
    /// an account whose data has already gone or race the worker that is erasing it.
    /// </summary>
    [Fact]
    public async Task Cancelling_AfterTheWindow_IsRefused_AndTheRequestStands()
    {
        var auth0Id = await SeedUserAsync();
        await BackdateRequestAsync(auth0Id, DateTime.UtcNow.AddDays(-31));

        var status = await WithService(s => s.CancelDeletionAsync(auth0Id));

        Assert.True(status!.DeletionRequested);
        Assert.False(status.CanCancel);

        var reread = await WithService(s => s.GetDeletionStatusAsync(auth0Id));
        Assert.True(reread!.DeletionRequested);
    }

    [Fact]
    public async Task Status_SaysItCannotBeCancelled_OnceTheWindowHasPassed()
    {
        var auth0Id = await SeedUserAsync();
        await BackdateRequestAsync(auth0Id, DateTime.UtcNow.AddDays(-31));

        var status = await WithService(s => s.GetDeletionStatusAsync(auth0Id));

        Assert.True(status!.DeletionRequested);
        Assert.False(status.CanCancel);
    }

    [Fact]
    public async Task AnIdentityWithNoAccount_IsNull_OnEveryCall()
    {
        const string stranger = "auth0|nobody";

        Assert.Null(await WithService(s => s.GetDeletionStatusAsync(stranger)));
        Assert.Null(await WithService(s => s.RequestDeletionAsync(stranger)));
        Assert.Null(await WithService(s => s.CancelDeletionAsync(stranger)));
    }

    /// <summary>
    /// Asking to be deleted gives up this account's push registrations, server-side.
    /// </summary>
    /// <remarks>
    /// The client releases its own before making the request, but it cannot be relied on for
    /// this: the request can come from a second device, the app's release is best-effort, and
    /// once the request is recorded the gate refuses the unregister endpoint anyway. A token left
    /// live stays deliverable — recipients are resolved on IsActive and ReceiveAlerts, never on
    /// DeletionRequestedAtUtc (issue #1144) — so a monitored person's alerts would go on reaching
    /// a phone whose owner asked to be erased, for the whole 30 days.
    /// </remarks>
    [Fact]
    public async Task Requesting_GivesUpThePushRegistrations()
    {
        var auth0Id = await SeedUserAsync();
        var userId = await UserIdAsync(auth0Id);
        await SeedPushTokenAsync(userId);

        await WithService(s => s.RequestDeletionAsync(auth0Id));

        using var scope = _services.CreateScope();
        var token = await scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>()
            .Set<PushDeviceToken>().AsNoTracking().SingleAsync(t => t.UserId == userId);

        Assert.NotNull(token.DisabledDate);
        Assert.Equal("Account deletion requested", token.DisabledReason);
    }

    /// <summary>
    /// A row already disabled — by a sign-out, or by the client's own release moments earlier —
    /// keeps the reason it was disabled for, and its place in the 30-day sweep.
    /// </summary>
    [Fact]
    public async Task Requesting_LeavesAnAlreadyDisabledRegistrationAlone()
    {
        var auth0Id = await SeedUserAsync();
        var userId = await UserIdAsync(auth0Id);
        var disabledAt = DateTime.UtcNow.AddDays(-2);
        await SeedPushTokenAsync(userId, disabledAt, "Unregistered by client");

        await WithService(s => s.RequestDeletionAsync(auth0Id));

        using var scope = _services.CreateScope();
        var token = await scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>()
            .Set<PushDeviceToken>().AsNoTracking().SingleAsync(t => t.UserId == userId);

        Assert.Equal("Unregistered by client", token.DisabledReason);
        Assert.Equal(disabledAt, token.DisabledDate!.Value, TimeSpan.FromSeconds(1));
    }

    private async Task<Guid> UserIdAsync(string auth0Id)
    {
        using var scope = _services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>()
            .Users.AsNoTracking().Where(u => u.Auth0UserId == auth0Id).Select(u => u.Id).SingleAsync();
    }

    private async Task SeedPushTokenAsync(
        Guid userId, DateTime? disabledDate = null, string? disabledReason = null)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        db.Add(new PushDeviceToken
        {
            UserId = userId,
            DeviceId = $"install-{Guid.NewGuid():N}",
            Platform = DevicePlatform.Android,
            AppVersion = "1.0+1",
            Token = "ciphertext",
            TokenFingerprint = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            OsAuthorizationStatus = OsAuthorizationStatus.Granted,
            LastSeenDate = DateTime.UtcNow,
            DisabledDate = disabledDate,
            DisabledReason = disabledReason,
        });
        await db.SaveChangesAsync();
    }

    private async Task<T> WithService<T>(Func<UserService, Task<T>> act)
    {
        using var scope = _services.CreateScope();
        var sut = new UserService(
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            Substitute.For<CardiTrack.Application.Interfaces.Services.INotificationGapResolver>());
        return await act(sut);
    }

    private async Task<string> SeedUserAsync()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        var organization = new Organization { Name = "Doe family", Type = OrganizationType.Family };
        db.Organizations.Add(organization);

        var auth0Id = $"auth0|{Guid.NewGuid():N}";
        db.Users.Add(new User
        {
            OrganizationId = organization.Id,
            Auth0UserId = auth0Id,
            Email = $"caregiver-{Guid.NewGuid():N}@example.com",
            Name = "Jane Doe",
        });
        await db.SaveChangesAsync();
        return auth0Id;
    }

    /// <summary>
    /// Moves an existing request back in time, so the day-31 cases do not need a clock to be
    /// injectable through three layers to be testable.
    /// </summary>
    private async Task BackdateRequestAsync(string auth0UserId, DateTime requestedAtUtc)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        await db.Users
            .Where(u => u.Auth0UserId == auth0UserId)
            .ExecuteUpdateAsync(set => set.SetProperty(u => u.DeletionRequestedAtUtc, requestedAtUtc));
    }
}
