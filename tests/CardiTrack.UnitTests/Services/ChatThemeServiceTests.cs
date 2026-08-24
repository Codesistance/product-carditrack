using CardiTrack.Application.DTOs.Common;
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
/// The theming pass — <see cref="ChatThemeService.ThemeDueSessionsAsync"/>. The parts worth
/// asserting are the ones nothing downstream would catch: the member's name never enters the
/// prompt (the DPIA A20 rule this job must uphold), the stored label is ciphertext, a leftover
/// placeholder resolves to the first name or refuses to store, and one failed generation costs
/// only its own session.
/// </summary>
public class ChatThemeServiceTests
{
    private readonly IRewriteAiService _rewriteAi = Substitute.For<IRewriteAiService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IMemberChatSessionRepository _sessions = Substitute.For<IMemberChatSessionRepository>();
    private readonly ICardiMemberRepository _members = Substitute.For<ICardiMemberRepository>();

    private readonly Guid _memberId = Guid.NewGuid();

    public ChatThemeServiceTests()
    {
        _unitOfWork.MemberChatSessions.Returns(_sessions);
        _unitOfWork.CardiMembers.Returns(_members);
        _members.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            Name = "Margaret Doe",
            DateOfBirth = new DateOnly(1950, 6, 1),
            IsActive = true,
        });
    }

    private ChatThemeService CreateSut() =>
        new(_rewriteAi, _unitOfWork, PromptContextFactory.Encryption, NullLogger<ChatThemeService>.Instance);

    private static string Stored(string plain) => PromptContextFactory.Encryption.Encrypt(plain);

    /// <summary>One unthemed completed session whose turns mention the member by name.</summary>
    private MemberChatSession ArrangeSession(params (ChatTurnRole Role, string Content)[] turns)
    {
        var session = new MemberChatSession
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            CardiMemberId = _memberId,
            StartedAtUtc = DateTime.UtcNow.AddHours(-5),
            LastTurnAtUtc = DateTime.UtcNow.AddHours(-5),
        };
        var withTurns = new MemberChatSession
        {
            Id = session.Id,
            UserId = session.UserId,
            CardiMemberId = _memberId,
            StartedAtUtc = session.StartedAtUtc,
            LastTurnAtUtc = session.LastTurnAtUtc,
            Turns = turns.Select((t, i) => new MemberChatTurn
            {
                SessionId = session.Id,
                Role = t.Role,
                Content = Stored(t.Content),
                CreatedAtUtc = session.StartedAtUtc.AddMinutes(i),
            }).ToList(),
        };
        _sessions.GetByIdWithTurnsAsync(session.Id, Arg.Any<CancellationToken>()).Returns(withTurns);
        return session;
    }

    private void Batch(params MemberChatSession[] sessions) =>
        _sessions.ListUnthemedCompletedAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(sessions);

    private void Generates(string theme) =>
        _rewriteAi.GenerateStructuredWithUsageAsync<ChatThemeService.ThemeAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AiGenerationResult<ChatThemeService.ThemeAiResponse>(
                new ChatThemeService.ThemeAiResponse { Theme = theme },
                new AiUsage { ModelName = "test-rewrite" }));

    [Fact]
    public async Task ThemesASession_AndStoresTheLabelEncrypted()
    {
        var session = ArrangeSession(
            (ChatTurnRole.User, "How did Margaret sleep this week?"),
            (ChatTurnRole.Assistant, "Margaret slept about as usual."));
        Batch(session);
        Generates("Sleep quality this week");

        var themed = await CreateSut().ThemeDueSessionsAsync(DateTime.UtcNow);

        Assert.Equal(1, themed);
        Assert.NotNull(session.Theme);
        Assert.NotEqual("Sleep quality this week", session.Theme);
        Assert.Equal("Sleep quality this week", PromptContextFactory.Encryption.Decrypt(session.Theme!));
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    /// <summary>The DPIA A20 rule this job exists under: stored turns hold the member's real
    /// name, and it must be swapped back out before the transcript reaches the Rewrite slot.</summary>
    [Fact]
    public async Task TheMembersName_NeverEntersThePrompt()
    {
        var session = ArrangeSession(
            (ChatTurnRole.User, "Is Margaret walking enough? Margaret Doe barely left the house."),
            (ChatTurnRole.Assistant, "Margaret's steps are below her usual."));
        Batch(session);

        string? prompt = null;
        _rewriteAi.GenerateStructuredWithUsageAsync<ChatThemeService.ThemeAiResponse>(
                Arg.Do<string>(p => prompt = p), Arg.Any<CancellationToken>())
            .Returns(new AiGenerationResult<ChatThemeService.ThemeAiResponse>(
                new ChatThemeService.ThemeAiResponse { Theme = "Daily activity concerns" },
                new AiUsage { ModelName = "test-rewrite" }));

        await CreateSut().ThemeDueSessionsAsync(DateTime.UtcNow);

        Assert.NotNull(prompt);
        Assert.DoesNotContain("Margaret", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CardiTrackCardiMember", prompt, StringComparison.Ordinal);
        Assert.Contains("Caregiver:", prompt, StringComparison.Ordinal);
    }

    /// <summary>One conversation whose generation fails is skipped — retried by a later pass —
    /// and must not cost the rest of the batch their labels.</summary>
    [Fact]
    public async Task AFailedGeneration_SkipsOnlyItsOwnSession()
    {
        var failing = ArrangeSession((ChatTurnRole.User, "How is she doing?"));
        var succeeding = ArrangeSession((ChatTurnRole.User, "Any alerts today?"));
        Batch(failing, succeeding);

        var calls = 0;
        _rewriteAi.GenerateStructuredWithUsageAsync<ChatThemeService.ThemeAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++calls == 1
                ? throw new HttpRequestException("rewrite host unreachable")
                : new AiGenerationResult<ChatThemeService.ThemeAiResponse>(
                    new ChatThemeService.ThemeAiResponse { Theme = "Recent alerts" },
                    new AiUsage { ModelName = "test-rewrite" }));

        var themed = await CreateSut().ThemeDueSessionsAsync(DateTime.UtcNow);

        Assert.Equal(1, themed);
        Assert.Null(failing.Theme);
        Assert.NotNull(succeeding.Theme);
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    /// <summary>A model that writes the placeholder into the label gets it resolved — the label
    /// is caregiver-facing, where the real name belongs — using the first name, the way replies
    /// already do.</summary>
    [Fact]
    public async Task APlaceholderInTheLabel_ResolvesToTheFirstName()
    {
        var session = ArrangeSession((ChatTurnRole.User, "How did she sleep?"));
        Batch(session);
        Generates("CardiTrackCardiMember's sleep this week");

        await CreateSut().ThemeDueSessionsAsync(DateTime.UtcNow);

        Assert.Equal("Margaret's sleep this week",
            PromptContextFactory.Encryption.Decrypt(session.Theme!));
    }

    [Theory]
    [InlineData("\"Sleep quality this week.\"", "Sleep quality this week")]
    [InlineData("   Alerts and heart rate!  ", "Alerts and heart rate")]
    public async Task TheLabelIsSanitized_BeforeItIsStored(string generated, string stored)
    {
        var session = ArrangeSession((ChatTurnRole.User, "How is she doing?"));
        Batch(session);
        Generates(generated);

        await CreateSut().ThemeDueSessionsAsync(DateTime.UtcNow);

        Assert.Equal(stored, PromptContextFactory.Encryption.Decrypt(session.Theme!));
    }

    /// <summary>The 60-character backstop: a runaway generation is cut to title length rather
    /// than stored as a paragraph.</summary>
    [Fact]
    public async Task AnOverlongLabel_IsCappedAtTitleLength()
    {
        var session = ArrangeSession((ChatTurnRole.User, "How is she doing?"));
        Batch(session);
        Generates(new string('a', 50) + " " + new string('b', 50));

        await CreateSut().ThemeDueSessionsAsync(DateTime.UtcNow);

        var theme = PromptContextFactory.Encryption.Decrypt(session.Theme!);
        Assert.True(theme.Length <= 60, $"stored theme is {theme.Length} chars");
    }

    /// <summary>An empty generation stores nothing — the row keeps its opening-question fallback
    /// and a later pass tries again.</summary>
    [Fact]
    public async Task AnEmptyGeneration_StoresNothing()
    {
        var session = ArrangeSession((ChatTurnRole.User, "How is she doing?"));
        Batch(session);
        Generates("   ");

        var themed = await CreateSut().ThemeDueSessionsAsync(DateTime.UtcNow);

        Assert.Equal(0, themed);
        Assert.Null(session.Theme);
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    /// <summary>Fail closed: with no member (or no name) there is nothing to redact against, and
    /// the transcript must not leave unredacted — no model call, no theme, retried by a later
    /// pass while the row keeps its opening-question fallback.</summary>
    [Fact]
    public async Task AMissingMember_SkipsTheSession_WithoutCallingTheModel()
    {
        var session = ArrangeSession((ChatTurnRole.User, "How did Margaret sleep?"));
        Batch(session);
        _members.GetByIdAsync(_memberId).Returns((CardiMember?)null);

        var themed = await CreateSut().ThemeDueSessionsAsync(DateTime.UtcNow);

        Assert.Equal(0, themed);
        Assert.Null(session.Theme);
        await _rewriteAi.DidNotReceiveWithAnyArgs()
            .GenerateStructuredWithUsageAsync<ChatThemeService.ThemeAiResponse>(default!, default);
    }

    [Fact]
    public async Task AnEmptyBatch_MakesNoModelCalls()
    {
        Batch();

        var themed = await CreateSut().ThemeDueSessionsAsync(DateTime.UtcNow);

        Assert.Equal(0, themed);
        await _rewriteAi.DidNotReceiveWithAnyArgs()
            .GenerateStructuredWithUsageAsync<ChatThemeService.ThemeAiResponse>(default!, default);
    }
}
