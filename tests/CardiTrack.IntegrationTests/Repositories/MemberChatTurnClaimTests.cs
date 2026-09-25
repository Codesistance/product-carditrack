using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using CardiTrack.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace CardiTrack.IntegrationTests.Repositories;

/// <summary>
/// The atomic claim behind a confirmed alert-settings change, against a real Postgres: the
/// guarantee is one conditional UPDATE, so it is worth nothing against a substitute. Claims once,
/// and never again — not for the same answer sent twice, and not once a newer reply has
/// superseded the proposal.
/// </summary>
public class MemberChatTurnClaimTests : IAsyncLifetime
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

    private async Task<(Guid SessionId, Guid TurnId)> SeedProposalAsync()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        var session = new MemberChatSession
        {
            CardiMemberId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-2),
            LastTurnAtUtc = DateTime.UtcNow.AddMinutes(-1),
        };
        db.MemberChatSessions.Add(session);

        var turn = new MemberChatTurn
        {
            SessionId = session.Id,
            Role = ChatTurnRole.Assistant,
            Workflow = MemberChatWorkflow.AlertSettings,
            Content = "ciphertext",
            PendingChange = "ciphertext-of-a-proposal",
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
        };
        db.MemberChatTurns.Add(turn);
        await db.SaveChangesAsync();

        return (session.Id, turn.Id);
    }

    [Fact]
    public async Task TheFirstClaimWins_AndTheSecondFindsNothing()
    {
        var (_, turnId) = await SeedProposalAsync();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        var repository = new MemberChatTurnRepository(db);

        Assert.True(await repository.TryClaimPendingChangeAsync(turnId));
        Assert.False(await repository.TryClaimPendingChangeAsync(turnId));

        var stored = await db.MemberChatTurns.AsNoTracking().SingleAsync(t => t.Id == turnId);
        Assert.Null(stored.PendingChange);
        Assert.Equal("ciphertext", stored.Content);
    }

    /// <summary>Two answers racing from two connections: exactly one takes the proposal.</summary>
    [Fact]
    public async Task ConcurrentClaims_ExactlyOneSucceeds()
    {
        var (_, turnId) = await SeedProposalAsync();

        var attempts = Enumerable.Range(0, 8).Select(async _ =>
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
            return await new MemberChatTurnRepository(db).TryClaimPendingChangeAsync(turnId);
        });

        var results = await Task.WhenAll(attempts);

        Assert.Equal(1, results.Count(claimed => claimed));
    }

    /// <summary>
    /// A proposal a newer reply has already superseded cannot be claimed: a "yes" that read the
    /// old turn before a concurrent question was answered finds nothing to apply.
    /// </summary>
    [Fact]
    public async Task ASupersededProposal_CannotBeClaimed()
    {
        var (sessionId, turnId) = await SeedProposalAsync();

        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
            db.MemberChatTurns.Add(new MemberChatTurn
            {
                SessionId = sessionId,
                Role = ChatTurnRole.Assistant,
                Workflow = MemberChatWorkflow.Status,
                Content = "a later reply",
                CreatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using var claimScope = _services.CreateScope();
        var claimDb = claimScope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        Assert.False(await new MemberChatTurnRepository(claimDb).TryClaimPendingChangeAsync(turnId));
        var stored = await claimDb.MemberChatTurns.AsNoTracking().SingleAsync(t => t.Id == turnId);
        Assert.NotNull(stored.PendingChange);
    }
}
