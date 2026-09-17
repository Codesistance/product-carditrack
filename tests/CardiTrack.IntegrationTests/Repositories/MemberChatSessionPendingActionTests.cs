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

    [Fact]
    public async Task TheFirstClaimGetsTheOffer_AndClearsTheRow()
    {
        var expires = DateTime.UtcNow.AddMinutes(10);
        var id = await SeedSessionAsync("Rewrite|Daybook|2026-09-13", expires);

        using var scope = _services.CreateScope();
        var repo = new MemberChatSessionRepository(scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>());

        var claimed = await repo.TryConsumePendingActionAsync(id);

        Assert.NotNull(claimed);
        Assert.Equal("Rewrite|Daybook|2026-09-13", claimed.Action);
        Assert.NotNull(claimed.ExpiresAtUtc);
        Assert.Equal(expires, claimed.ExpiresAtUtc.Value, TimeSpan.FromMilliseconds(1));

        var stored = await ReadBackAsync(id);
        Assert.Null(stored.PendingAction);
        Assert.Null(stored.PendingActionExpiresAtUtc);
    }

    /// <summary>The second request to see the same offer gets nothing — the row was the lock.</summary>
    [Fact]
    public async Task TheSecondClaimGetsNothing()
    {
        var id = await SeedSessionAsync("Discard|Weekbook|2026-09-13", DateTime.UtcNow.AddMinutes(10));

        using var scope = _services.CreateScope();
        var repo = new MemberChatSessionRepository(scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>());

        Assert.NotNull(await repo.TryConsumePendingActionAsync(id));
        Assert.Null(await repo.TryConsumePendingActionAsync(id));
    }

    [Fact]
    public async Task ASessionWithNoOffer_YieldsNothing_AndIsLeftAlone()
    {
        var id = await SeedSessionAsync(null, null);

        using var scope = _services.CreateScope();
        var repo = new MemberChatSessionRepository(scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>());

        Assert.Null(await repo.TryConsumePendingActionAsync(id));
        Assert.Null(await repo.TryConsumePendingActionAsync(Guid.NewGuid()));
    }
}
