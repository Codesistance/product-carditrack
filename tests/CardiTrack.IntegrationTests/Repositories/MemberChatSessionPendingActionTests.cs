using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Persistence;
using CardiTrack.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace CardiTrack.IntegrationTests.Repositories;

/// <summary>
/// The claim-and-clear statement behind the chat's confirmation turn. Against a real Postgres,
/// because the whole guarantee — an offer is honoured once, whoever asks first — is one SQL
/// statement's atomicity, and a substitute at the repository boundary would pass whatever the
/// statement said.
/// </summary>
public class MemberChatSessionPendingActionTests : IAsyncLifetime
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
        await _services.DisposeAsync();
        await _container.DisposeAsync();
    }

    private async Task<Guid> SeedSessionAsync(string? pendingAction, DateTime? expiresAt)
    {
        using var scope = _services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        var session = new MemberChatSession
        {
            UserId = Guid.NewGuid(),
            CardiMemberId = Guid.NewGuid(),
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            LastTurnAtUtc = DateTime.UtcNow.AddMinutes(-1),
            PendingAction = pendingAction,
            PendingActionExpiresAtUtc = expiresAt,
        };
        context.MemberChatSessions.Add(session);
        await context.SaveChangesAsync();
        return session.Id;
    }

    private async Task<MemberChatSession> ReadBackAsync(Guid id)
    {
        using var scope = _services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        return await context.MemberChatSessions.AsNoTracking().SingleAsync(s => s.Id == id);
    }

    /// <summary>The session as a turn would hold it: tracked by the context the repository shares.</summary>
    private static async Task<(MemberChatSessionRepository Repo, MemberChatSession Tracked, CardiTrackDbContext Context)> LoadAsync(
        IServiceScope scope, Guid id)
    {
        var context = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        var tracked = await context.MemberChatSessions.SingleAsync(s => s.Id == id);
        return (new MemberChatSessionRepository(context), tracked, context);
    }

    [Fact]
    public async Task TheFirstClaimGetsTheOffer_AndClearsTheRow()
    {
        var expires = DateTime.UtcNow.AddMinutes(10);
        var id = await SeedSessionAsync("Rewrite|Daybook|2026-09-13", expires);

        using var scope = _services.CreateScope();
        var (repo, tracked, _) = await LoadAsync(scope, id);

        var claimed = await repo.TryConsumePendingActionAsync(tracked);

        Assert.NotNull(claimed);
        Assert.Equal("Rewrite|Daybook|2026-09-13", claimed.Action);
        Assert.NotNull(claimed.ExpiresAtUtc);
        Assert.Equal(expires, claimed.ExpiresAtUtc.Value, TimeSpan.FromMilliseconds(1));
        Assert.Null(tracked.PendingAction);

        var stored = await ReadBackAsync(id);
        Assert.Null(stored.PendingAction);
        Assert.Null(stored.PendingActionExpiresAtUtc);
    }

    /// <summary>
    /// Two requests that both read the same offer — two contexts, two tracked copies — and only
    /// the first claim gets it. The row was the lock.
    /// </summary>
    [Fact]
    public async Task TheSecondClaimOnTheSameOfferGetsNothing()
    {
        var id = await SeedSessionAsync("Discard|Weekbook|2026-09-13", DateTime.UtcNow.AddMinutes(10));

        using var first = _services.CreateScope();
        using var second = _services.CreateScope();
        var (repoA, trackedA, _) = await LoadAsync(first, id);
        var (repoB, trackedB, _) = await LoadAsync(second, id);

        Assert.NotNull(await repoA.TryConsumePendingActionAsync(trackedA));
        Assert.Null(await repoB.TryConsumePendingActionAsync(trackedB));
    }

    /// <summary>
    /// The race the statement exists for, with both transactions open at once: the first claim
    /// holds the row inside its still-uncommitted transaction, and the second — instead of waiting
    /// on that commit — skips the locked row and gets nothing. Only after the first commits does
    /// the row read as cleared.
    /// </summary>
    [Fact]
    public async Task AClaimSkipsARowAnotherOpenTransactionHolds()
    {
        var id = await SeedSessionAsync("Rewrite|Daybook|2026-09-10", DateTime.UtcNow.AddMinutes(10));

        using var first = _services.CreateScope();
        using var second = _services.CreateScope();
        var (repoA, trackedA, contextA) = await LoadAsync(first, id);
        var (repoB, trackedB, _) = await LoadAsync(second, id);

        await using var winning = await contextA.Database.BeginTransactionAsync();
        var claimedA = await repoA.TryConsumePendingActionAsync(trackedA);
        Assert.NotNull(claimedA);

        // While A's transaction is still open, B must return at once with nothing — not block.
        var loser = repoB.TryConsumePendingActionAsync(trackedB);
        var finished = await Task.WhenAny(loser, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(loser, finished);
        Assert.Null(await loser);

        await winning.CommitAsync();

        var stored = await ReadBackAsync(id);
        Assert.Null(stored.PendingAction);
    }

    /// <summary>
    /// A yes answers the offer it was reading. When a newer offer has replaced it in between, the
    /// claim misses, the newer offer stays on the row, and this request's later save must not
    /// overwrite it with the nulls it holds in memory.
    /// </summary>
    [Fact]
    public async Task AClaimForAReplacedOffer_GetsNothing_AndLeavesTheNewerOfferAlone()
    {
        var id = await SeedSessionAsync("Discard|Daybook|2026-09-10", DateTime.UtcNow.AddMinutes(10));

        using var stale = _services.CreateScope();
        var (repo, tracked, context) = await LoadAsync(stale, id);

        // Another turn replaces the offer after this one loaded the session.
        using (var other = _services.CreateScope())
        {
            var (otherRepo, otherTracked, otherContext) = await LoadAsync(other, id);
            otherTracked.PendingAction = "Rewrite|Weekbook|2026-09-13";
            otherTracked.PendingActionExpiresAtUtc = DateTime.UtcNow.AddMinutes(10);
            await otherContext.SaveChangesAsync();
            _ = otherRepo;
        }

        Assert.Null(await repo.TryConsumePendingActionAsync(tracked));

        // The stale turn saves as every turn does; the newer offer must survive it.
        tracked.LastTurnAtUtc = DateTime.UtcNow;
        await context.SaveChangesAsync();

        var stored = await ReadBackAsync(id);
        Assert.Equal("Rewrite|Weekbook|2026-09-13", stored.PendingAction);
    }

    /// <summary>
    /// The same request offered again is a new offer: the line matches, the moment does not, and
    /// the stale yes must not take it.
    /// </summary>
    [Fact]
    public async Task AClaimForTheSameOfferReissued_GetsNothing()
    {
        var id = await SeedSessionAsync("Discard|Daybook|2026-09-10", DateTime.UtcNow.AddMinutes(10));

        using var stale = _services.CreateScope();
        var (repo, tracked, _) = await LoadAsync(stale, id);

        var reissuedAt = DateTime.UtcNow.AddMinutes(12);
        using (var other = _services.CreateScope())
        {
            var (_, otherTracked, otherContext) = await LoadAsync(other, id);
            otherTracked.PendingActionExpiresAtUtc = reissuedAt;
            await otherContext.SaveChangesAsync();
        }

        Assert.Null(await repo.TryConsumePendingActionAsync(tracked));

        var stored = await ReadBackAsync(id);
        Assert.Equal("Discard|Daybook|2026-09-10", stored.PendingAction);
        Assert.NotNull(stored.PendingActionExpiresAtUtc);
        Assert.Equal(reissuedAt, stored.PendingActionExpiresAtUtc.Value, TimeSpan.FromMilliseconds(1));
    }

    /// <summary>
    /// After a claim, an offer the same turn sets afresh is written — the cleared original values
    /// must not swallow it.
    /// </summary>
    [Fact]
    public async Task ANewOfferSetAfterTheClaim_IsStillSaved()
    {
        var id = await SeedSessionAsync("Discard|Daybook|2026-09-10", DateTime.UtcNow.AddMinutes(10));

        using var scope = _services.CreateScope();
        var (repo, tracked, context) = await LoadAsync(scope, id);
        Assert.NotNull(await repo.TryConsumePendingActionAsync(tracked));

        tracked.PendingAction = "Discard|Daybook|2026-09-10";
        tracked.PendingActionExpiresAtUtc = DateTime.UtcNow.AddMinutes(10);
        await context.SaveChangesAsync();

        Assert.Equal("Discard|Daybook|2026-09-10", (await ReadBackAsync(id)).PendingAction);
    }

    /// <summary>The ordinary offer: a clear row takes it, and the turn's save has nothing to add.</summary>
    [Fact]
    public async Task AnOfferLandsOnAClearRow()
    {
        var id = await SeedSessionAsync(null, null);
        var expires = DateTime.UtcNow.AddMinutes(10);

        using var scope = _services.CreateScope();
        var (repo, tracked, context) = await LoadAsync(scope, id);

        Assert.True(await repo.TryOfferPendingActionAsync(tracked, "Discard|Daybook|2026-09-10", expires));
        tracked.LastTurnAtUtc = DateTime.UtcNow;
        await context.SaveChangesAsync();

        var stored = await ReadBackAsync(id);
        Assert.Equal("Discard|Daybook|2026-09-10", stored.PendingAction);
        Assert.NotNull(stored.PendingActionExpiresAtUtc);
        Assert.Equal(expires, stored.PendingActionExpiresAtUtc.Value, TimeSpan.FromMilliseconds(1));
    }

    /// <summary>
    /// Two turns that both loaded a clear session and both want to offer: the second is refused
    /// and the first offer stands — including through the loser's own save.
    /// </summary>
    [Fact]
    public async Task ASecondOfferAgainstARowAnotherTurnFilled_IsRefused_AndTheFirstStands()
    {
        var id = await SeedSessionAsync(null, null);

        using var first = _services.CreateScope();
        using var second = _services.CreateScope();
        var (repoA, trackedA, _) = await LoadAsync(first, id);
        var (repoB, trackedB, contextB) = await LoadAsync(second, id);

        Assert.True(await repoA.TryOfferPendingActionAsync(trackedA, "Discard|Daybook|2026-09-10", DateTime.UtcNow.AddMinutes(10)));
        Assert.False(await repoB.TryOfferPendingActionAsync(trackedB, "Rewrite|Weekbook|2026-09-13", DateTime.UtcNow.AddMinutes(10)));

        trackedB.LastTurnAtUtc = DateTime.UtcNow;
        await contextB.SaveChangesAsync();

        Assert.Equal("Discard|Daybook|2026-09-10", (await ReadBackAsync(id)).PendingAction);
    }

    [Fact]
    public async Task ASessionWithNoOffer_YieldsNothing_AndIsLeftAlone()
    {
        var id = await SeedSessionAsync(null, null);

        using var scope = _services.CreateScope();
        var (repo, tracked, _) = await LoadAsync(scope, id);

        Assert.Null(await repo.TryConsumePendingActionAsync(tracked));
        Assert.Null((await ReadBackAsync(id)).PendingAction);
    }
}
