using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;
using CardiTrack.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
            Options.Create(new ChatRoutingSettings()),
            NullLogger<MemberChatService>.Instance);

    private static string Stored(string plain) => PromptContextFactory.Encryption.Encrypt(plain);

    private MemberChatSessionListing Listing(
        string? storedFirstQuestion, int questionCount, DateTime lastTurnAtUtc) => new()
    {
        Session = new MemberChatSession
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            CardiMemberId = _memberId,
            StartedAtUtc = lastTurnAtUtc.AddMinutes(-10),
            LastTurnAtUtc = lastTurnAtUtc,
        },
        FirstQuestionContent = storedFirstQuestion,
        QuestionCount = questionCount,
    };

    [Fact]
    public async Task GetSessions_DecryptsTheOpeningQuestion_AndKeepsTheRepositoryOrder()
    {
        var now = DateTime.UtcNow;
        _sessions.ListForMemberAsync(_userId, _memberId, Arg.Any<CancellationToken>())
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

    /// <summary>A session that never got a caregiver question — a send that failed before its
    /// first turn persisted — has nothing a list row can be recognised by, so it never becomes
    /// one.</summary>
    [Fact]
    public async Task GetSessions_OmitsASessionWithNoOpeningQuestion()
    {
        _sessions.ListForMemberAsync(_userId, _memberId, Arg.Any<CancellationToken>())
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

        await _sessions.DidNotReceiveWithAnyArgs().ListForMemberAsync(default, default, default);
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
}
