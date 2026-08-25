using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The history list's two reads — <see cref="MemberChatService.GetSessionsAsync"/> and
/// <see cref="MemberChatService.GetSessionAsync"/>. The parts worth asserting are the ones the
/// repository cannot see: the opening question is decrypted before it leaves, a session nobody
/// asked anything in never becomes a row, and a guessed session id — another caregiver's, or the
/// same caregiver's about a different member — gets the same 404 as one that never existed.
/// </summary>
public class MemberChatHistoryListTests
{
    private readonly IMedicalAiService _medicalAi = Substitute.For<IMedicalAiService>();
    private readonly IRewriteAiService _rewriteAi = Substitute.For<IRewriteAiService>();
    private readonly IDataQueryPlanner _planner = Substitute.For<IDataQueryPlanner>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICardiMemberAccessService _access = Substitute.For<ICardiMemberAccessService>();
    private readonly IMemberChatSessionRepository _sessions = Substitute.For<IMemberChatSessionRepository>();

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();

    public MemberChatHistoryListTests()
    {
        _unitOfWork.MemberChatSessions.Returns(_sessions);
    }

    private MemberChatService CreateSut() =>
        new(_medicalAi, _rewriteAi, _planner, Substitute.For<IChatRouter>(), _unitOfWork, _access,
            PromptContextFactory.Composer(_unitOfWork), PromptContextFactory.Encryption,
            NullLogger<MemberChatService>.Instance);

    private static string Stored(string plain) => PromptContextFactory.Encryption.Encrypt(plain);

    private MemberChatSessionListing Listing(
        string? storedFirstQuestion, int questionCount, DateTime lastTurnAtUtc,
        string? storedTheme = null) => new()
    {
        Session = new MemberChatSession
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            CardiMemberId = _memberId,
            StartedAtUtc = lastTurnAtUtc.AddMinutes(-10),
            LastTurnAtUtc = lastTurnAtUtc,
            Theme = storedTheme,
        },
        FirstQuestionContent = storedFirstQuestion,
        QuestionCount = questionCount,
    };

    [Fact]
    public async Task GetSessions_DecryptsTheOpeningQuestion_AndKeepsTheRepositoryOrder()
    {
        var now = DateTime.UtcNow;
        _sessions.ListCompletedForMemberAsync(_userId, _memberId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([
                Listing(Stored("Any alerts today?"), 1, now),
                Listing(Stored("How did he sleep?"), 3, now.AddDays(-1)),
            ]);

        var result = await CreateSut().GetSessionsAsync(_userId, _memberId);

        Assert.Equal(2, result.Sessions.Count);
        Assert.Equal("Any alerts today?", result.Sessions[0].FirstQuestion);
        Assert.Equal("How did he sleep?", result.Sessions[1].FirstQuestion);
        Assert.Equal(3, result.Sessions[1].QuestionCount);
    }

    /// <summary>The stored theme leaves decrypted; a session the theming job hasn't visited
    /// carries null, and the client falls back to the opening question.</summary>
    [Fact]
    public async Task GetSessions_DecryptsTheTheme_AndLeavesAnUnthemedSessionNull()
    {
        _sessions.ListCompletedForMemberAsync(_userId, _memberId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([
                Listing(Stored("How did he sleep?"), 2, DateTime.UtcNow, storedTheme: Stored("Sleep quality this week")),
                Listing(Stored("Any alerts today?"), 1, DateTime.UtcNow.AddDays(-1)),
            ]);

        var result = await CreateSut().GetSessionsAsync(_userId, _memberId);

        Assert.Equal("Sleep quality this week", result.Sessions[0].Theme);
        Assert.Null(result.Sessions[1].Theme);
    }

    /// <summary>A session that never got a caregiver question — a send that failed before its
    /// first turn persisted — has nothing a list row can be recognised by, so it never becomes
    /// one.</summary>
    [Fact]
    public async Task GetSessions_OmitsASessionWithNoOpeningQuestion()
    {
        _sessions.ListCompletedForMemberAsync(_userId, _memberId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([
                Listing(null, 0, DateTime.UtcNow),
                Listing(Stored("How active was she this week?"), 1, DateTime.UtcNow.AddHours(-2)),
            ]);

        var result = await CreateSut().GetSessionsAsync(_userId, _memberId);

        var row = Assert.Single(result.Sessions);
        Assert.Equal("How active was she this week?", row.FirstQuestion);
    }

    [Fact]
    public async Task GetSessions_ChecksViewAccessFirst()
    {
        _access.RequireViewAccessAsync(_userId, _memberId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new KeyNotFoundException("CardiMember not found."));

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().GetSessionsAsync(_userId, _memberId));

        await _sessions.DidNotReceiveWithAnyArgs().ListCompletedForMemberAsync(default, default, default, default);
    }

    [Fact]
    public async Task EndCurrentSession_MarksTheActiveSessionEnded_AndSaysWhichItWas()
    {
        var active = new MemberChatSession
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            CardiMemberId = _memberId,
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-30),
            LastTurnAtUtc = DateTime.UtcNow.AddMinutes(-5),
        };
        _sessions.GetActiveAsync(_userId, _memberId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(active);

        var result = await CreateSut().EndCurrentSessionAsync(_userId, _memberId);

        Assert.Equal(active.Id, result.EndedSessionId);
        Assert.NotNull(active.EndedAtUtc);
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    /// <summary>Nothing active is a fine outcome, not an error — the caregiver asked for a fresh
    /// start and has one either way. Nothing is written.</summary>
    [Fact]
    public async Task EndCurrentSession_WithNothingActive_IsANoOp()
    {
        _sessions.GetActiveAsync(_userId, _memberId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns((MemberChatSession?)null);

        var result = await CreateSut().EndCurrentSessionAsync(_userId, _memberId);

        Assert.Null(result.EndedSessionId);
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    [Fact]
    public async Task ContinueSession_ReopensIt_EndsTheActiveOne_AndReturnsTheTurns()
    {
        var sessionId = Guid.NewGuid();
        var completed = new MemberChatSession
        {
            Id = sessionId,
            UserId = _userId,
            CardiMemberId = _memberId,
            StartedAtUtc = DateTime.UtcNow.AddDays(-2),
            LastTurnAtUtc = DateTime.UtcNow.AddDays(-2),
            EndedAtUtc = DateTime.UtcNow.AddDays(-2),
        };
        var active = new MemberChatSession
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            CardiMemberId = _memberId,
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-20),
            LastTurnAtUtc = DateTime.UtcNow.AddMinutes(-5),
        };
        var withTurns = new MemberChatSession
        {
            Id = sessionId,
            UserId = _userId,
            CardiMemberId = _memberId,
            StartedAtUtc = completed.StartedAtUtc,
            LastTurnAtUtc = DateTime.UtcNow,
            Turns =
            [
                new MemberChatTurn
                {
                    SessionId = sessionId, Role = ChatTurnRole.User,
                    Content = Stored("How did he sleep?"), CreatedAtUtc = DateTime.UtcNow.AddDays(-2),
                },
            ],
        };
        _sessions.GetByIdAsync(sessionId).Returns(completed);
        _sessions.GetActiveAsync(_userId, _memberId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(active);
        _sessions.GetByIdWithTurnsAsync(sessionId, Arg.Any<CancellationToken>()).Returns(withTurns);

        var before = DateTime.UtcNow;
        var result = await CreateSut().ContinueSessionAsync(_userId, _memberId, sessionId);

        Assert.Null(completed.EndedAtUtc);
        Assert.True(completed.LastTurnAtUtc >= before, "continuing must bring the session back inside the active window");
        Assert.NotNull(active.EndedAtUtc);
        Assert.Equal(sessionId, result.SessionId);
        var turn = Assert.Single(result.Turns);
        Assert.Equal("How did he sleep?", turn.Content);
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    /// <summary>Same existence-hiding 404 as reading a session: another caregiver's conversation,
    /// or the same caregiver's about a different member, is indistinguishable from one that never
    /// existed.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ContinueSession_TreatsAForeignSessionAsNotFound(bool otherCaregiver, bool otherMember)
    {
        var session = new MemberChatSession
        {
            Id = Guid.NewGuid(),
            UserId = otherCaregiver ? Guid.NewGuid() : _userId,
            CardiMemberId = otherMember ? Guid.NewGuid() : _memberId,
            StartedAtUtc = DateTime.UtcNow.AddDays(-1),
            LastTurnAtUtc = DateTime.UtcNow.AddDays(-1),
        };
        _sessions.GetByIdAsync(session.Id).Returns(session);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().ContinueSessionAsync(_userId, _memberId, session.Id));

        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    [Fact]
    public async Task GetSession_ReturnsDecryptedTurns_ForTheCaregiversOwnSession()
    {
        var sessionId = Guid.NewGuid();
        var session = new MemberChatSession
        {
            Id = sessionId,
            UserId = _userId,
            CardiMemberId = _memberId,
            StartedAtUtc = DateTime.UtcNow.AddDays(-2),
            LastTurnAtUtc = DateTime.UtcNow.AddDays(-2),
            Turns =
            [
                new MemberChatTurn
                {
                    SessionId = sessionId, Role = ChatTurnRole.User,
                    Content = Stored("How did he sleep?"), CreatedAtUtc = DateTime.UtcNow.AddDays(-2),
                },
                new MemberChatTurn
                {
                    SessionId = sessionId, Role = ChatTurnRole.Assistant,
                    Content = Stored("About as usual."), CreatedAtUtc = DateTime.UtcNow.AddDays(-2),
                },
            ],
        };
        _sessions.GetByIdWithTurnsAsync(session.Id, Arg.Any<CancellationToken>()).Returns(session);

        var result = await CreateSut().GetSessionAsync(_userId, _memberId, session.Id);

        Assert.Equal(session.Id, result.SessionId);
        Assert.Equal(2, result.Turns.Count);
        Assert.Equal("How did he sleep?", result.Turns[0].Content);
        Assert.Equal("Assistant", result.Turns[1].Role);
        Assert.Equal("About as usual.", result.Turns[1].Content);
    }

    /// <summary>Both halves of ownership get the same existence-hiding 404 as a session that
    /// never existed — a guessed id must learn nothing.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task GetSession_TreatsAForeignSessionAsNotFound(bool otherCaregiver, bool otherMember)
    {
        var session = new MemberChatSession
        {
            Id = Guid.NewGuid(),
            UserId = otherCaregiver ? Guid.NewGuid() : _userId,
            CardiMemberId = otherMember ? Guid.NewGuid() : _memberId,
            StartedAtUtc = DateTime.UtcNow,
            LastTurnAtUtc = DateTime.UtcNow,
        };
        _sessions.GetByIdWithTurnsAsync(session.Id, Arg.Any<CancellationToken>()).Returns(session);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().GetSessionAsync(_userId, _memberId, session.Id));
    }

    [Fact]
    public async Task GetSession_TreatsAMissingSessionAsNotFound()
    {
        _sessions.GetByIdWithTurnsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((MemberChatSession?)null);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().GetSessionAsync(_userId, _memberId, Guid.NewGuid()));
    }

    /// <summary>
    /// The permanent delete removes exactly what the fetch returned and reports the count. The
    /// fetch predicate is the security boundary — ownership is part of the query, not a check
    /// after it — so the predicate itself is evaluated here against an owned session, another
    /// caregiver's, and another member's.
    /// </summary>
    [Fact]
    public async Task DeleteSessions_RemovesOwnedRows_AndTheFetchPredicateIsTheOwnershipCheck()
    {
        var owned = new MemberChatSession { Id = Guid.NewGuid(), UserId = _userId, CardiMemberId = _memberId };
        System.Linq.Expressions.Expression<Func<MemberChatSession, bool>>? predicate = null;
        _sessions.FindAsync(Arg.Do<System.Linq.Expressions.Expression<Func<MemberChatSession, bool>>>(p => predicate = p))
            .Returns([owned]);

        var result = await CreateSut().DeleteSessionsAsync(_userId, _memberId, [owned.Id, Guid.NewGuid()]);

        Assert.Equal(1, result.DeletedCount);
        _sessions.Received(1).RemoveRange(Arg.Is<IEnumerable<MemberChatSession>>(s => s.Single() == owned));
        await _unitOfWork.Received(1).SaveChangesAsync();

        var matches = predicate!.Compile();
        Assert.True(matches(owned));
        Assert.False(matches(new MemberChatSession { Id = owned.Id, UserId = Guid.NewGuid(), CardiMemberId = _memberId }));
        Assert.False(matches(new MemberChatSession { Id = owned.Id, UserId = _userId, CardiMemberId = Guid.NewGuid() }));
    }

    /// <summary>Nothing owned matches — a guessed id, or a list refreshed elsewhere — and the
    /// batch is a quiet zero: nothing removed, nothing saved, nothing learned.</summary>
    [Fact]
    public async Task DeleteSessions_WithNothingOwned_DeletesNothingAndSaysZero()
    {
        _sessions.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<MemberChatSession, bool>>>())
            .Returns([]);

        var result = await CreateSut().DeleteSessionsAsync(_userId, _memberId, [Guid.NewGuid()]);

        Assert.Equal(0, result.DeletedCount);
        _sessions.DidNotReceiveWithAnyArgs().RemoveRange(default!);
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    [Fact]
    public async Task DeleteSessions_WithAnEmptyRequest_TouchesNothing()
    {
        var result = await CreateSut().DeleteSessionsAsync(_userId, _memberId, []);

        Assert.Equal(0, result.DeletedCount);
        await _sessions.DidNotReceiveWithAnyArgs().FindAsync(default!);
    }

    /// <summary>The member-level access check still gates the whole call — a caregiver who may
    /// not view the member deletes nothing about them.</summary>
    [Fact]
    public async Task DeleteSessions_WithoutViewAccess_Throws()
    {
        _access.RequireViewAccessAsync(_userId, _memberId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new KeyNotFoundException("We couldn't find that member."));

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().DeleteSessionsAsync(_userId, _memberId, [Guid.NewGuid()]));
        _sessions.DidNotReceiveWithAnyArgs().RemoveRange(default!);
    }
}
