using System.Security.Cryptography;
using System.Text.Json;
using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Reading a stored conversation back for an export. Two things are load-bearing here: that a
/// session which is not this caregiver's own thread about this member is indistinguishable from
/// one that never existed, and that a row which will not decrypt costs a line rather than the
/// whole export.
/// </summary>
public class ChatTranscriptSourceTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IMemberChatSessionRepository _sessions = Substitute.For<IMemberChatSessionRepository>();
    private readonly IEncryptionService _encryption = Substitute.For<IEncryptionService>();

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();

    public ChatTranscriptSourceTests()
    {
        _unitOfWork.MemberChatSessions.Returns(_sessions);

        // The fake cipher is the plaintext with a marker on the front, so a test can tell a
        // decrypted value from one that was never encrypted.
        _encryption.Encrypt(Arg.Any<string>()).Returns(c => "enc:" + c.Arg<string>());
        _encryption.Decrypt(Arg.Any<string>()).Returns(c => c.Arg<string>()["enc:".Length..]);
    }

    private ChatTranscriptSource CreateSut() => new(_unitOfWork, _encryption);

    private MemberChatSession BuildSession(
        Guid? userId = null,
        Guid? memberId = null,
        string? theme = "enc:Sleep over the week",
        ICollection<MemberChatTurn>? turns = null) => new()
    {
        Id = _sessionId,
        UserId = userId ?? _userId,
        CardiMemberId = memberId ?? _memberId,
        StartedAtUtc = new DateTime(2026, 2, 10, 9, 14, 0, DateTimeKind.Unspecified),
        LastTurnAtUtc = new DateTime(2026, 2, 10, 9, 20, 0, DateTimeKind.Unspecified),
        Theme = theme,
        Turns = turns ??
        [
            new MemberChatTurn
            {
                Role = ChatTurnRole.User,
                Content = "enc:How has she been sleeping?",
                CreatedAtUtc = new DateTime(2026, 2, 10, 9, 14, 0, DateTimeKind.Unspecified),
            },
            new MemberChatTurn
            {
                Role = ChatTurnRole.Assistant,
                Content = "enc:A little longer each night.",
                Charts = "enc:" + JsonSerializer.Serialize(new[]
                {
                    new ChartSeries("Sleep", [new ChartPoint(new DateOnly(2026, 2, 9), 402)]),
                }),
                CreatedAtUtc = new DateTime(2026, 2, 10, 9, 15, 0, DateTimeKind.Unspecified),
            },
        ],
    };

    [Fact]
    public async Task Get_ReturnsTheDecryptedConversation()
    {
        _sessions.GetByIdWithTurnsAsync(_sessionId, Arg.Any<CancellationToken>())
            .Returns(BuildSession());

        var transcript = await CreateSut().GetAsync(_userId, _memberId, _sessionId);

        Assert.Equal("Sleep over the week", transcript.Theme);
        Assert.Equal(2, transcript.Turns.Count);
        Assert.Equal("How has she been sleeping?", transcript.Turns[0].Content);
        Assert.Equal(ChatTurnRole.Assistant, transcript.Turns[1].Role);
        Assert.Equal(1, transcript.QuestionCount);
    }

    [Fact]
    public async Task Get_ReturnsTheChartsTheReplyCarried()
    {
        _sessions.GetByIdWithTurnsAsync(_sessionId, Arg.Any<CancellationToken>())
            .Returns(BuildSession());

        var transcript = await CreateSut().GetAsync(_userId, _memberId, _sessionId);

        var charts = transcript.Turns[1].Charts;
        Assert.Single(charts);
        Assert.Equal("Sleep", charts[0].Metric);
        Assert.Equal(402, charts[0].Points[0].Value);
    }

    [Fact]
    public async Task Get_StampsTimesAsUtc()
    {
        // The rows are stored Unspecified. A document that printed them as local would be dated
        // by whichever server rendered it.
        _sessions.GetByIdWithTurnsAsync(_sessionId, Arg.Any<CancellationToken>())
            .Returns(BuildSession());

        var transcript = await CreateSut().GetAsync(_userId, _memberId, _sessionId);

        Assert.Equal(TimeSpan.Zero, transcript.StartedAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, transcript.Turns[0].CreatedAtUtc.Offset);
    }

    [Fact]
    public async Task Get_OrdersTurnsAsTheyHappened()
    {
        var reply = new MemberChatTurn
        {
            Role = ChatTurnRole.Assistant,
            Content = "enc:Second.",
            CreatedAtUtc = new DateTime(2026, 2, 10, 9, 15, 0, DateTimeKind.Unspecified),
        };
        var question = new MemberChatTurn
        {
            Role = ChatTurnRole.User,
            Content = "enc:First.",
            CreatedAtUtc = new DateTime(2026, 2, 10, 9, 14, 0, DateTimeKind.Unspecified),
        };
        _sessions.GetByIdWithTurnsAsync(_sessionId, Arg.Any<CancellationToken>())
            .Returns(BuildSession(turns: [reply, question]));

        var transcript = await CreateSut().GetAsync(_userId, _memberId, _sessionId);

        Assert.Equal("First.", transcript.Turns[0].Content);
    }

    [Fact]
    public async Task Get_HidesAnotherCaregiversConversation()
    {
        _sessions.GetByIdWithTurnsAsync(_sessionId, Arg.Any<CancellationToken>())
            .Returns(BuildSession(userId: Guid.NewGuid()));

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().GetAsync(_userId, _memberId, _sessionId));
    }

    [Fact]
    public async Task Get_HidesAConversationAboutAnotherMember()
    {
        _sessions.GetByIdWithTurnsAsync(_sessionId, Arg.Any<CancellationToken>())
            .Returns(BuildSession(memberId: Guid.NewGuid()));

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().GetAsync(_userId, _memberId, _sessionId));
    }

    [Fact]
    public async Task Get_HidesAConversationThatDoesNotExist()
    {
        _sessions.GetByIdWithTurnsAsync(_sessionId, Arg.Any<CancellationToken>())
            .Returns((MemberChatSession?)null);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().GetAsync(_userId, _memberId, _sessionId));
    }

    [Fact]
    public async Task Get_CostsOneLine_WhenATurnWillNotDecrypt()
    {
        // A row written under a rotated key. Losing a caregiver the whole copy of a conversation
        // over one unreadable line is the wrong trade — the document says the line could not be
        // read instead.
        _encryption.Decrypt("enc:unreadable").Returns(_ => throw new CryptographicException());
        _sessions.GetByIdWithTurnsAsync(_sessionId, Arg.Any<CancellationToken>())
            .Returns(BuildSession(turns:
            [
                new MemberChatTurn
                {
                    Role = ChatTurnRole.User,
                    Content = "enc:unreadable",
                    CreatedAtUtc = new DateTime(2026, 2, 10, 9, 14, 0, DateTimeKind.Unspecified),
                },
            ]));

        var transcript = await CreateSut().GetAsync(_userId, _memberId, _sessionId);

        Assert.Equal(string.Empty, transcript.Turns[0].Content);
    }

    [Fact]
    public async Task Get_CostsTheCharts_WhenTheirBlobWillNotParse()
    {
        _sessions.GetByIdWithTurnsAsync(_sessionId, Arg.Any<CancellationToken>())
            .Returns(BuildSession(turns:
            [
                new MemberChatTurn
                {
                    Role = ChatTurnRole.Assistant,
                    Content = "enc:Here's what I found.",
                    Charts = "enc:{not json",
                    CreatedAtUtc = new DateTime(2026, 2, 10, 9, 15, 0, DateTimeKind.Unspecified),
                },
            ]));

        var transcript = await CreateSut().GetAsync(_userId, _memberId, _sessionId);

        Assert.Empty(transcript.Turns[0].Charts);
        Assert.Equal("Here's what I found.", transcript.Turns[0].Content);
    }

    [Fact]
    public async Task RequireOwned_AcceptsTheirOwnConversation_WithoutReadingIt()
    {
        // The gate answers yes or no. Decrypting a conversation to decide it would materialise
        // the plaintext of a health conversation on a request that is about to discard it.
        _sessions.GetByIdAsync(_sessionId).Returns(BuildSession());

        await CreateSut().RequireOwnedAsync(_userId, _memberId, _sessionId);

        _encryption.DidNotReceive().Decrypt(Arg.Any<string>());
        await _sessions.DidNotReceive().GetByIdWithTurnsAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RequireOwned_HidesAConversationThatIsNotTheirs(bool otherUser, bool otherMember)
    {
        _sessions.GetByIdAsync(_sessionId).Returns(BuildSession(
            userId: otherUser ? Guid.NewGuid() : _userId,
            memberId: otherMember ? Guid.NewGuid() : _memberId));

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().RequireOwnedAsync(_userId, _memberId, _sessionId));
    }

    [Fact]
    public async Task RequireOwned_HidesAConversationThatDoesNotExist()
    {
        _sessions.GetByIdAsync(_sessionId).Returns((MemberChatSession?)null);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().RequireOwnedAsync(_userId, _memberId, _sessionId));
    }

    [Fact]
    public async Task Label_FallsBackToTheOpeningQuestion_WhileThemingHasNotVisited()
    {
        _sessions.GetByIdWithTurnsAsync(_sessionId, Arg.Any<CancellationToken>())
            .Returns(BuildSession(theme: null));

        var transcript = await CreateSut().GetAsync(_userId, _memberId, _sessionId);

        Assert.Null(transcript.Theme);
        Assert.Equal("How has she been sleeping?", transcript.Label);
    }
}
