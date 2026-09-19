using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using CardiTrack.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace CardiTrack.IntegrationTests.Notifications;

/// <summary>
/// Two reachability filters found missing in PR #183 review round 1 — both are query-shape
/// concerns (a wrong <c>WHERE</c> clause, not wrong C#), so they need a real database rather than
/// a substitute repository to prove the SQL actually excludes what it should.
/// </summary>
public class PushRepositoryFiltersTests : IAsyncLifetime
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

        _services = sc.BuildServiceProvider();

        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>().Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _container.DisposeAsync();
    }

    // ── NotificationDeliveryRepository.GetDueForEscalationAsync ────────────────────

    [Fact]
    public async Task GetDueForEscalationAsync_ExcludesARowSentLessThan120sAgo()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        var now = DateTime.UtcNow;

        db.NotificationDeliveries.Add(SentDelivery(sentDate: now.AddSeconds(-30)));
        await db.SaveChangesAsync();

        var repo = new NotificationDeliveryRepository(db);
        var due = await repo.GetDueForEscalationAsync(now);

        Assert.Empty(due);
    }

    [Fact]
    public async Task GetDueForEscalationAsync_IncludesARowAtExactly120s()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        var now = DateTime.UtcNow;

        var delivery = SentDelivery(sentDate: now.AddSeconds(-120));
        db.NotificationDeliveries.Add(delivery);
        await db.SaveChangesAsync();

        var repo = new NotificationDeliveryRepository(db);
        var due = await repo.GetDueForEscalationAsync(now);

        Assert.Equal(delivery.Id, Assert.Single(due).Id);
    }

    private static NotificationDelivery SentDelivery(DateTime sentDate) => new()
    {
        SourceType = DeliverySourceType.Alert,
        SourceId = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        Category = DeliveryCategory.Safety,
        Channel = DeliveryChannel.Push,
        State = DeliveryState.Sent,
        DedupKey = $"test:escalation:{Guid.NewGuid()}",
        ExpiresAt = sentDate.AddMinutes(30),
        SentDate = sentDate,
        EscalationStage = EscalationStage.Initial
    };

    // ── PushDeviceTokenRepository.GetLiveForUserAsync ──────────────────────────────

    [Fact]
    public async Task GetLiveForUserAsync_ExcludesAnOsDeniedToken_RegardlessOfCategory()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        var userId = Guid.NewGuid();

        db.PushDeviceTokens.Add(Token(userId, OsAuthorizationStatus.Denied, safetyChannelEnabled: true));
        await db.SaveChangesAsync();

        var repo = new PushDeviceTokenRepository(db);

        Assert.Empty(await repo.GetLiveForUserAsync(userId, DeliveryCategory.Safety));
        Assert.Empty(await repo.GetLiveForUserAsync(userId, DeliveryCategory.Nudge));
    }

    [Fact]
    public async Task GetLiveForUserAsync_SafetyChannelMuted_ExcludesOnlySafetyCategory()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        var userId = Guid.NewGuid();

        var token = Token(userId, OsAuthorizationStatus.Granted, safetyChannelEnabled: false);
        db.PushDeviceTokens.Add(token);
        await db.SaveChangesAsync();

        var repo = new PushDeviceTokenRepository(db);

        // A caregiver who muted the OS-level Safety channel specifically still gets Health/Nudge
        // pushes on their other channels — SafetyChannelEnabled must not blanket-filter every
        // category.
        Assert.Empty(await repo.GetLiveForUserAsync(userId, DeliveryCategory.Safety));
        Assert.Equal(token.Id, Assert.Single(await repo.GetLiveForUserAsync(userId, DeliveryCategory.Health)).Id);
        Assert.Equal(token.Id, Assert.Single(await repo.GetLiveForUserAsync(userId, DeliveryCategory.Nudge)).Id);
    }

    [Fact]
    public async Task GetLiveForUserAsync_GrantedAndSafetyEnabled_IsLiveForEveryCategory()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        var userId = Guid.NewGuid();

        var token = Token(userId, OsAuthorizationStatus.Granted, safetyChannelEnabled: true);
        db.PushDeviceTokens.Add(token);
        await db.SaveChangesAsync();

        var repo = new PushDeviceTokenRepository(db);

        Assert.Equal(token.Id, Assert.Single(await repo.GetLiveForUserAsync(userId, DeliveryCategory.Safety)).Id);
    }

    // ── Awaiting account deletion (#1144) ──────────────────────────────────────────

    /// <summary>
    /// A caregiver who has asked to be deleted receives nothing at all. <c>EnqueueAsync</c>
    /// declines to create a delivery for one, and this is the other half: what was already queued
    /// when the request landed, and every retry and escalation that follows it.
    /// </summary>
    [Fact]
    public async Task GetLiveForUserAsync_ExcludesATokenWhoseOwnerIsAwaitingDeletion()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        var userId = await SeedUserAsync(db, deletionRequestedAt: DateTime.UtcNow.AddDays(-2));

        db.PushDeviceTokens.Add(Token(userId, OsAuthorizationStatus.Granted, safetyChannelEnabled: true));
        await db.SaveChangesAsync();

        var repo = new PushDeviceTokenRepository(db);

        Assert.Empty(await repo.GetLiveForUserAsync(userId, DeliveryCategory.Safety));
        Assert.Empty(await repo.GetLiveForUserAsync(userId, DeliveryCategory.Health));
    }

    /// <summary>
    /// Cancelling restores it with nothing else to undo — the filter reads the column rather than
    /// a copy of it taken when the request was made. This is also why the token being disabled at
    /// request time is not enough on its own: signing in to cancel re-registers the device, which
    /// would have made queued pushes deliverable again if this query did not check.
    /// </summary>
    [Fact]
    public async Task GetLiveForUserAsync_IncludesItAgainOnceTheDeletionIsCancelled()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        var userId = await SeedUserAsync(db, deletionRequestedAt: DateTime.UtcNow.AddDays(-2));

        var token = Token(userId, OsAuthorizationStatus.Granted, safetyChannelEnabled: true);
        db.PushDeviceTokens.Add(token);
        await db.SaveChangesAsync();

        var user = await db.Users.SingleAsync(u => u.Id == userId);
        user.DeletionRequestedAtUtc = null;
        await db.SaveChangesAsync();

        var repo = new PushDeviceTokenRepository(db);

        Assert.Equal(token.Id, Assert.Single(await repo.GetLiveForUserAsync(userId, DeliveryCategory.Safety)).Id);
    }

    [Fact]
    public async Task GetDueForLivenessProbeAsync_ExcludesATokenWhoseOwnerIsAwaitingDeletion()
    {
        // The probe is a silent push, and an account on its way out has nothing left to prove
        // about its reachability.
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        var awaiting = await SeedUserAsync(db, deletionRequestedAt: DateTime.UtcNow.AddDays(-2));
        var staying = await SeedUserAsync(db, deletionRequestedAt: null);

        db.PushDeviceTokens.Add(Token(awaiting, OsAuthorizationStatus.Granted, safetyChannelEnabled: true));
        var stays = Token(staying, OsAuthorizationStatus.Granted, safetyChannelEnabled: true);
        db.PushDeviceTokens.Add(stays);
        await db.SaveChangesAsync();

        var repo = new PushDeviceTokenRepository(db);
        var due = await repo.GetDueForLivenessProbeAsync(DateTime.UtcNow);

        Assert.Equal(stays.Id, Assert.Single(due).Id);
    }

    private static async Task<Guid> SeedUserAsync(CardiTrackDbContext db, DateTime? deletionRequestedAt)
    {
        var organization = new Organization { Name = "Doe family", Type = OrganizationType.Family };
        db.Organizations.Add(organization);

        var user = new User
        {
            OrganizationId = organization.Id,
            Auth0UserId = $"auth0|{Guid.NewGuid():N}",
            Email = $"caregiver-{Guid.NewGuid():N}@example.com",
            Name = "Jane Doe",
            Role = UserRole.Member,
            IsActive = true,
            DeletionRequestedAtUtc = deletionRequestedAt,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        return user.Id;
    }

    private static PushDeviceToken Token(Guid userId, OsAuthorizationStatus status, bool safetyChannelEnabled) => new()
    {
        UserId = userId,
        DeviceId = $"device-{Guid.NewGuid():N}",
        Platform = DevicePlatform.Android,
        Token = "ciphertext",
        TokenFingerprint = Guid.NewGuid().ToString("N"),
        OsAuthorizationStatus = status,
        SafetyChannelEnabled = safetyChannelEnabled
    };
}
