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
    /// Deleting the session must take its turns with it, and the token ledger with them — a chat
    /// record cannot be "erased" while its messages or their usage accounting survive, which is
    /// exactly the GDPR Art. 17 guarantee <see cref="MemberChatSession"/>'s remarks describe.
    /// </summary>
    [Fact]
    public async Task Remove_CascadesToTurnsAndUsage_LeavesNoRowBehind()
    {
        using var scope = fixture.CreateScope();
        var userId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var session = Session(userId, memberId, now);
        var turn = Turn(session.Id, ChatTurnRole.User, "How has she been sleeping?", now);
        var usage = new MemberChatTurnUsage
        {
            Id = Guid.NewGuid(),
            TurnId = turn.Id,
            Step = AiCallStep.ClinicalAnalysis,
            ProviderSlot = AiProviderSlot.Private,
            ModelName = "medgemma-test",
            InputTokens = 512,
            OutputTokens = 24,
            DurationMs = 1800,
        };

        var sessionRepo = scope.ServiceProvider.GetRequiredService<IMemberChatSessionRepository>();
        var turnRepo = scope.ServiceProvider.GetRequiredService<IMemberChatTurnRepository>();
        var usageRepo = scope.ServiceProvider.GetRequiredService<IMemberChatTurnUsageRepository>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await sessionRepo.AddAsync(session);
        await turnRepo.AddAsync(turn);
        await usageRepo.AddAsync(usage);
        await uow.SaveChangesAsync();

        var tracked = await sessionRepo.GetByIdAsync(session.Id);
        sessionRepo.Remove(tracked!);
        await uow.SaveChangesAsync();

        using var reading = fixture.CreateScope();
        var db = reading.ServiceProvider
            .GetRequiredService<CardiTrack.Infrastructure.Persistence.CardiTrackDbContext>();
        Assert.Equal(0, await db.MemberChatSessions.AsNoTracking().CountAsync(s => s.Id == session.Id));
        Assert.Equal(0, await db.MemberChatTurns.AsNoTracking().CountAsync(t => t.SessionId == session.Id));
        Assert.Equal(0, await db.MemberChatTurnUsages.AsNoTracking().CountAsync(u => u.TurnId == turn.Id));
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

    /// <summary>
    /// The history list's one read: completed conversations only, newest started first, each
    /// carrying its opening caregiver question and its question count — computed in SQL, so this
    /// is the projection worth proving against the database it runs on.
    /// </summary>
    [Fact]
    public async Task ListCompletedForMemberAsync_ReturnsNewestStartedFirst_WithOpeningQuestionAndQuestionCount()
    {
        using var scope = fixture.CreateScope();
        var userId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var older = Session(userId, memberId, now.AddDays(-2));
        var newer = Session(userId, memberId, now.AddDays(-1));
        var otherCaregivers = Session(Guid.NewGuid(), memberId, now.AddDays(-1));
        var aboutSomeoneElse = Session(userId, Guid.NewGuid(), now.AddDays(-1));

        var sessionRepo = scope.ServiceProvider.GetRequiredService<IMemberChatSessionRepository>();
        var turnRepo = scope.ServiceProvider.GetRequiredService<IMemberChatTurnRepository>();
        foreach (var s in new[] { older, newer, otherCaregivers, aboutSomeoneElse })
            await sessionRepo.AddAsync(s);
        // The older session: two questions with a reply between them — the count is caregiver
        // questions only, and the opening question is the earliest, not the latest.
        await turnRepo.AddAsync(Turn(older.Id, ChatTurnRole.User, "How did he sleep?", now.AddDays(-2).AddMinutes(-9)));
        await turnRepo.AddAsync(Turn(older.Id, ChatTurnRole.Assistant, "About as usual.", now.AddDays(-2).AddMinutes(-8)));
        await turnRepo.AddAsync(Turn(older.Id, ChatTurnRole.User, "And the night before?", now.AddDays(-2)));
        await turnRepo.AddAsync(Turn(newer.Id, ChatTurnRole.User, "Any alerts today?", now.AddDays(-1)));
        await turnRepo.AddAsync(Turn(otherCaregivers.Id, ChatTurnRole.User, "Not this caregiver's", now.AddDays(-1)));
        await turnRepo.AddAsync(Turn(aboutSomeoneElse.Id, ChatTurnRole.User, "Not this member's", now.AddDays(-1)));
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();

        var listings = await sessionRepo.ListCompletedForMemberAsync(userId, memberId, now.AddHours(-2));

        Assert.Equal(new[] { newer.Id, older.Id }, listings.Select(l => l.Session.Id));
        Assert.Equal("Any alerts today?", listings[0].FirstQuestionContent);
        Assert.Equal(1, listings[0].QuestionCount);
        Assert.Equal("How did he sleep?", listings[1].FirstQuestionContent);
        Assert.Equal(2, listings[1].QuestionCount);
    }

    /// <summary>
    /// The list is the complement of "active": a conversation still inside the window and not
    /// ended is the one the chat window is having, and it must not double as a history row —
    /// while an *ended* conversation belongs to history at once, however fresh its last turn.
    /// </summary>
    [Fact]
    public async Task ListCompletedForMemberAsync_ExcludesTheActiveSession_ButIncludesAnEndedOne()
    {
        using var scope = fixture.CreateScope();
        var userId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var active = Session(userId, memberId, now);
        var endedJustNow = Session(userId, memberId, now.AddMinutes(-5));
        endedJustNow.EndedAtUtc = now;
        var lapsed = Session(userId, memberId, now.AddHours(-3));

        var sessionRepo = scope.ServiceProvider.GetRequiredService<IMemberChatSessionRepository>();
        foreach (var s in new[] { active, endedJustNow, lapsed })
            await sessionRepo.AddAsync(s);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();

        var listings = await sessionRepo.ListCompletedForMemberAsync(userId, memberId, now.AddHours(-2));

        Assert.Equal(2, listings.Count);
        Assert.DoesNotContain(active.Id, listings.Select(l => l.Session.Id));
        Assert.Contains(endedJustNow.Id, listings.Select(l => l.Session.Id));
        Assert.Contains(lapsed.Id, listings.Select(l => l.Session.Id));
    }

    /// <summary>A session whose only turn is the assistant's has no opening question to be
    /// recognised by — the projection says so with a null, and the service omits the row.</summary>
    [Fact]
    public async Task ListCompletedForMemberAsync_SessionWithNoCaregiverTurn_HasNullOpeningQuestion()
    {
        using var scope = fixture.CreateScope();
        var userId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var session = Session(userId, memberId, now.AddHours(-3));

        var sessionRepo = scope.ServiceProvider.GetRequiredService<IMemberChatSessionRepository>();
        var turnRepo = scope.ServiceProvider.GetRequiredService<IMemberChatTurnRepository>();
        await sessionRepo.AddAsync(session);
        await turnRepo.AddAsync(Turn(session.Id, ChatTurnRole.Assistant, "An orphaned reply", now.AddHours(-3)));
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();

        var listings = await sessionRepo.ListCompletedForMemberAsync(userId, memberId, now.AddHours(-2));

        var listing = Assert.Single(listings);
        Assert.Null(listing.FirstQuestionContent);
        Assert.Equal(0, listing.QuestionCount);
    }

    /// <summary>An explicitly ended conversation is never "the one to continue", however recent
    /// its last turn — that is the whole point of ending it.</summary>
    [Fact]
    public async Task GetActiveAsync_IgnoresAnEndedSession_EvenInsideTheWindow()
    {
        using var scope = fixture.CreateScope();
        var userId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var ended = Session(userId, memberId, now);
        ended.EndedAtUtc = now;

        var sessionRepo = scope.ServiceProvider.GetRequiredService<IMemberChatSessionRepository>();
        await sessionRepo.AddAsync(ended);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();

        var active = await sessionRepo.GetActiveAsync(userId, memberId, now.AddHours(-2));

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
