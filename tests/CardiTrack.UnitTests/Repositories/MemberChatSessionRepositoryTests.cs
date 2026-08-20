using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.UnitTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CardiTrack.UnitTests.Repositories;

/// <summary>
/// Against the real PostgreSQL container, same reasoning as
/// <see cref="MemberQuestionnaireRepositoryTests"/>: the delete has to be a real one, and the
/// active-session lookup is an indexed query worth proving against the database it will actually run
/// on.
/// </summary>
[Collection("DatabaseCollection")]
public class MemberChatSessionRepositoryTests(TestDatabaseFixture fixture)
{
    private static MemberChatSession Session(Guid userId, Guid memberId, DateTime lastTurnAtUtc) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        CardiMemberId = memberId,
        StartedAtUtc = lastTurnAtUtc,
        LastTurnAtUtc = lastTurnAtUtc,
    };

    private static MemberChatTurn Turn(Guid sessionId, ChatTurnRole role, string content, DateTime createdAtUtc) => new()
    {
        Id = Guid.NewGuid(),
        SessionId = sessionId,
        Role = role,
        Content = content,
        CreatedAtUtc = createdAtUtc,
    };

    /// <summary>
    /// Deleting the session must take its turns with it — a chat record cannot be "erased" while
    /// its messages survive, which is exactly the GDPR Art. 17 guarantee
    /// <see cref="MemberChatSession"/>'s remarks describe.
    /// </summary>
    [Fact]
    public async Task Remove_CascadesToTurns_LeavesNoRowBehind()
    {
        using var scope = fixture.CreateScope();
        var userId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var session = Session(userId, memberId, now);
        var turn = Turn(session.Id, ChatTurnRole.User, "How has she been sleeping?", now);

        var sessionRepo = scope.ServiceProvider.GetRequiredService<IMemberChatSessionRepository>();
        var turnRepo = scope.ServiceProvider.GetRequiredService<IMemberChatTurnRepository>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await sessionRepo.AddAsync(session);
        await turnRepo.AddAsync(turn);
        await uow.SaveChangesAsync();

        var tracked = await sessionRepo.GetByIdAsync(session.Id);
        sessionRepo.Remove(tracked!);
        await uow.SaveChangesAsync();

        using var reading = fixture.CreateScope();
        var db = reading.ServiceProvider
            .GetRequiredService<CardiTrack.Infrastructure.Persistence.CardiTrackDbContext>();
        Assert.Equal(0, await db.MemberChatSessions.AsNoTracking().CountAsync(s => s.Id == session.Id));
        Assert.Equal(0, await db.MemberChatTurns.AsNoTracking().CountAsync(t => t.SessionId == session.Id));
    }

    [Fact]
    public async Task GetActiveAsync_ReturnsNull_WhenLastTurnIsBeforeTheActiveWindow()
    {
        using var scope = fixture.CreateScope();
        var userId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        var staleSession = Session(userId, memberId, DateTime.UtcNow.AddHours(-3));

        var sessionRepo = scope.ServiceProvider.GetRequiredService<IMemberChatSessionRepository>();
        await sessionRepo.AddAsync(staleSession);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();

        var active = await sessionRepo.GetActiveAsync(userId, memberId, DateTime.UtcNow.AddHours(-1));

        Assert.Null(active);
    }

    [Fact]
    public async Task GetByIdWithTurnsAsync_ReturnsTurnsOldestFirst()
    {
        using var scope = fixture.CreateScope();
        var userId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var session = Session(userId, memberId, now);
        var first = Turn(session.Id, ChatTurnRole.User, "First question", now.AddMinutes(-5));
        var second = Turn(session.Id, ChatTurnRole.Assistant, "First answer", now);

        var sessionRepo = scope.ServiceProvider.GetRequiredService<IMemberChatSessionRepository>();
        var turnRepo = scope.ServiceProvider.GetRequiredService<IMemberChatTurnRepository>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await sessionRepo.AddAsync(session);
        await turnRepo.AddAsync(first);
        await turnRepo.AddAsync(second);
        await uow.SaveChangesAsync();

        var loaded = await sessionRepo.GetByIdWithTurnsAsync(session.Id);

        Assert.NotNull(loaded);
        Assert.Equal(new[] { first.Id, second.Id }, loaded!.Turns.Select(t => t.Id));
    }
}
