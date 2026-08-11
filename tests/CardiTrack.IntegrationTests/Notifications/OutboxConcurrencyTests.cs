using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using CardiTrack.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace CardiTrack.IntegrationTests.Notifications;

/// <summary>
/// The actual proof that <c>NotificationDeliveryRepository.ClaimDueAsync</c>'s
/// <c>FOR UPDATE SKIP LOCKED</c> query does what <c>NotificationDispatchWorker</c> depends on
/// (notification_engine.md §13): several Cloud Run instances calling it against the same real
/// Postgres database at once divide the outbox rather than duplicate a send. A unit test with a
/// substitute repository cannot show this — the guarantee lives entirely in Postgres row-locking
/// semantics, so this needs a real database and genuinely concurrent callers.
/// </summary>
public class OutboxConcurrencyTests : IAsyncLifetime
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

    private async Task SeedPendingRowsAsync(int count)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        for (var i = 0; i < count; i++)
        {
            db.NotificationDeliveries.Add(new NotificationDelivery
            {
                SourceType = DeliverySourceType.Notification,
                SourceId = Guid.NewGuid(),
                UserId = Guid.NewGuid(),
                Category = DeliveryCategory.Nudge,
                Channel = DeliveryChannel.Push,
                State = DeliveryState.Pending,
                DedupKey = $"test:outbox-concurrency:{Guid.NewGuid()}",
                ExpiresAt = DateTime.UtcNow.AddMinutes(30)
            });
        }

        await db.SaveChangesAsync();
    }

    private NotificationDeliveryRepository CreateRepository(out IServiceScope scope)
    {
        scope = _services.CreateScope();
        return new NotificationDeliveryRepository(scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>());
    }

    [Fact]
    public async Task ClaimDueAsync_TwoConcurrentCallers_ClaimDisjointSets_AndTogetherClaimEverything()
    {
        await SeedPendingRowsAsync(40);
        var now = DateTime.UtcNow;

        // Each caller gets its own DbContext/scope — exactly what two Cloud Run instances (or
        // two ticks racing past each other) would look like, hitting the same physical database.
        var repoA = CreateRepository(out var scopeA);
        var repoB = CreateRepository(out var scopeB);
        try
        {
            var claimTaskA = repoA.ClaimDueAsync(batchSize: 25, utcNow: now);
            var claimTaskB = repoB.ClaimDueAsync(batchSize: 25, utcNow: now);
            var results = await Task.WhenAll(claimTaskA, claimTaskB);

            var claimedByA = results[0].Select(d => d.Id).ToHashSet();
            var claimedByB = results[1].Select(d => d.Id).ToHashSet();

            Assert.Empty(claimedByA.Intersect(claimedByB));
            Assert.Equal(40, claimedByA.Count + claimedByB.Count);
        }
        finally
        {
            scopeA.Dispose();
            scopeB.Dispose();
        }
    }

    [Fact]
    public async Task ClaimDueAsync_AClaimedRow_IsNotReclaimedByTheNextTick_WithinTheLeaseWindow()
    {
        await SeedPendingRowsAsync(5);
        var now = DateTime.UtcNow;

        var repo = CreateRepository(out var scope);
        try
        {
            var firstClaim = await repo.ClaimDueAsync(batchSize: 10, utcNow: now);
            Assert.Equal(5, firstClaim.Count);

            // Simulate the very next dispatch tick, moments later — well inside the 90-second
            // claim lease. Without the lease advancing NextAttemptAt on claim, this second call
            // would re-claim (and the worker would re-send) rows the first caller is still
            // processing; SKIP LOCKED alone only blocks a still-open transaction, not this.
            var secondClaim = await repo.ClaimDueAsync(batchSize: 10, utcNow: now.AddSeconds(5));

            Assert.Empty(secondClaim);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [Fact]
    public async Task ClaimDueAsync_ARowPastItsLease_BecomesClaimableAgain()
    {
        await SeedPendingRowsAsync(1);
        var now = DateTime.UtcNow;

        var repo = CreateRepository(out var scope);
        try
        {
            var firstClaim = await repo.ClaimDueAsync(batchSize: 10, utcNow: now);
            Assert.Single(firstClaim);

            // Past the 90-second lease — the crashed-worker self-heal path (§13): a claimed row
            // that never got a real outcome persisted becomes claimable again rather than stuck.
            var afterLeaseExpiry = await repo.ClaimDueAsync(batchSize: 10, utcNow: now.AddSeconds(91));

            Assert.Single(afterLeaseExpiry);
            Assert.Equal(firstClaim[0].Id, afterLeaseExpiry[0].Id);
        }
        finally
        {
            scope.Dispose();
        }
    }
}
