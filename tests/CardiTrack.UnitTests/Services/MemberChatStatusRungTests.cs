using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The status rung's two branches — §5 gives it two and only one was ever built.
/// </summary>
/// <remarks>
/// Every status-routed question got the liveness reply, so a caregiver asking "how are they doing
/// today?" was told "I can't see what Dad is doing right now": a refusal to a question they had not
/// asked, spending the first forty words of the answer. Meanwhile the sentence that did answer it
/// was already rendered in the dashboard header two centimetres above the chat bubble.
/// <para>
/// Which branch runs is the triage call's <c>isAboutThisMoment</c>, which already ran on every
/// message and whose prompt draws exactly this line: "a question about a period, however recent, is
/// not this", naming "how many steps today" as a no.
/// </para>
/// </remarks>
public class MemberChatStatusRungTests
{
    private readonly IMedicalAiService _medicalAi = Substitute.For<IMedicalAiService>();
    private readonly IRewriteAiService _rewriteAi = Substitute.For<IRewriteAiService>();
    private readonly IDataQueryPlanner _planner = Substitute.For<IDataQueryPlanner>();
    private readonly IChatRouter _router = Substitute.For<IChatRouter>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICardiMemberAccessService _access = Substitute.For<ICardiMemberAccessService>();

    private readonly IMemberChatSessionRepository _sessions = Substitute.For<IMemberChatSessionRepository>();
    private readonly IMemberChatTurnRepository _turns = Substitute.For<IMemberChatTurnRepository>();
    private readonly IMemberStatusLineRepository _statusLines = Substitute.For<IMemberStatusLineRepository>();
    private readonly IActivityLogRepository _activity = Substitute.For<IActivityLogRepository>();

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();
    private readonly CardiMember _member;

    public MemberChatStatusRungTests()
    {
        _member = new CardiMember
        {
            Id = _memberId,
            Name = "Moses Doe",
            DateOfBirth = new DateOnly(1948, 3, 15),
            IsActive = true,
        };

        _unitOfWork.CardiMembers.Returns(Substitute.For<ICardiMemberRepository>());
        _unitOfWork.MemberAdvises.Returns(Substitute.For<IMemberAdviseRepository>());
        _unitOfWork.MemberChatSessions.Returns(_sessions);
        _unitOfWork.MemberChatTurns.Returns(_turns);
        _unitOfWork.MemberChatTurnUsages.Returns(Substitute.For<IMemberChatTurnUsageRepository>());
        _unitOfWork.MemberStatusLines.Returns(_statusLines);
        _unitOfWork.ActivityLogs.Returns(_activity);

        _unitOfWork.CardiMembers.GetByIdAsync(_memberId).Returns(_member);
        _sessions.GetActiveAsync(_userId, _memberId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns((MemberChatSession?)null);

        Triage(aboutThisMoment: false);
        _router.RouteAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new AiGenerationResult<ChatRouteDecision>(
                new ChatRouteDecision { Primary = MemberChatWorkflow.Status },
                new AiUsage { ModelName = "test-router" }));
    }

    private void Triage(bool aboutThisMoment) =>
        _rewriteAi.GenerateStructuredWithUsageAsync<MemberChatService.MaliciousCheckAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AiGenerationResult<MemberChatService.MaliciousCheckAiResponse>(
                new MemberChatService.MaliciousCheckAiResponse
                {
                    IsMalicious = false,
                    IsCasualOrSocial = false,
                    IsOffTopic = false,
                    IsAboutThisMoment = aboutThisMoment,
                    IsAskingForAdvice = false,
                },
                new AiUsage { ModelName = "test-rewrite" }));

    private void StatusLineIs(string message, TimeSpan age) =>
        _statusLines.GetByCardiMemberAsync(_memberId).Returns(new MemberStatusLine
        {
            Message = message,
            Headline = "Settling",
            GeneratedAtUtc = DateTime.UtcNow - age,
        });

    private void ReadingsAre(params ActivityLog[] logs) =>
        _activity.GetByCardiMemberAndDateRangeAsync(
                _memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(logs);

    private MemberChatService CreateSut() =>
        new(_medicalAi, _rewriteAi, _planner, _router, _unitOfWork, _access,
            PromptContextFactory.Composer(_unitOfWork), PromptContextFactory.Encryption,
            NullLogger<MemberChatService>.Instance);

    // ── A question that names a reading ─────────────────────────────────────────

    /// <summary>
    /// The owner's own session (dev, 2026-09-07): "how is his heart rate" → "Steps are lower today
    /// than yesterday." A true caption about the wrong reading. The named reading leads, dated and
    /// beside yesterday's; the caption follows only because it is about something else.
    /// </summary>
    [Fact]
    public async Task AMetricQuestion_LeadsWithThatMetric_NeverTheStepsCaption()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        StatusLineIs("Steps are lower today than yesterday.", TimeSpan.FromHours(1));
        ReadingsAre(
            new ActivityLog { Date = today.AddDays(-1), Steps = 4905, RestingHeartRate = 68 },
            new ActivityLog { Date = today, Steps = 1200, RestingHeartRate = 70 });

        var reply = await CreateSut().SendMessageAsync(_userId, _memberId, "how is his heart rate");

        Assert.StartsWith(
            "The most recent resting heart rate I have for Moses is 70 bpm, today so far; yesterday it was 68 bpm.",
            reply.Reply, StringComparison.Ordinal);
        Assert.True(
            reply.Reply.IndexOf("70 bpm", StringComparison.Ordinal)
            < reply.Reply.IndexOf("Steps are lower", StringComparison.Ordinal),
            "the reading the caregiver asked about has to come before a caption about another one.");
        Assert.Contains("On the whole: Settling — Steps are lower today than yesterday.", reply.Reply, StringComparison.Ordinal);
        await _planner.DidNotReceiveWithAnyArgs().PlanAsync(default!, default, default, default);
    }

    /// <summary>
    /// "His specific measurements" is a request for the figures, not for the sentence that
    /// summarises them — every reading the day carries, and no caption.
    /// </summary>
    [Fact]
    public async Task AReadingsQuestion_ListsEveryMetric_NotTheCaption()
    {
        StatusLineIs("Steps are lower today than yesterday.", TimeSpan.FromHours(1));
        ReadingsAre(new ActivityLog
        {
            Date = DateOnly.FromDateTime(DateTime.UtcNow),
            Steps = 1200,
            RestingHeartRate = 70,
            SleepMinutes = 370,
            HeartRateVariabilityMs = 42.4m,
            OvernightBreathingRate = 14.2m,
            SpO2Average = 96.4m,
        });

        var reply = await CreateSut().SendMessageAsync(_userId, _memberId, "his specific measurements");

        Assert.StartsWith("The most recent readings I have for Moses are today so far:", reply.Reply, StringComparison.Ordinal);
        Assert.Contains("1,200 steps", reply.Reply, StringComparison.Ordinal);
        Assert.Contains("a resting heart rate of 70 bpm", reply.Reply, StringComparison.Ordinal);
        Assert.Contains("of sleep the night before", reply.Reply, StringComparison.Ordinal);
        Assert.Contains("an overnight heart rate variability of 42 ms", reply.Reply, StringComparison.Ordinal);
        Assert.Contains("14.2 breaths a minute overnight", reply.Reply, StringComparison.Ordinal);
        Assert.Contains("96.4% blood oxygen", reply.Reply, StringComparison.Ordinal);
        Assert.DoesNotContain("Steps are lower", reply.Reply, StringComparison.Ordinal);
    }

    /// <summary>A caption about the reading just stated is the same fact twice, so it is dropped;
    /// one about a different reading is the rest of the picture, so it follows.</summary>
    [Fact]
    public void TheCaptionFollowsAMetricReply_OnlyWhenItIsAboutSomethingElse()
    {
        var today = new DateOnly(2026, 9, 7);
        var line = new MemberStatusLine { Message = "Steps are lower today than yesterday." };
        var readings = new[] { new ActivityLog { Date = today, Steps = 1200, RestingHeartRate = 70 } };

        var aboutSteps = MemberChatReplies.StatusReply("Dad", "how are his steps", line, readings, today);
        var aboutHeart = MemberChatReplies.StatusReply("Dad", "how is his heart rate", line, readings, today);

        Assert.DoesNotContain("On the whole", aboutSteps, StringComparison.Ordinal);
        Assert.Contains("On the whole: Steps are lower today than yesterday.", aboutHeart, StringComparison.Ordinal);
    }

    /// <summary>Sleep belongs to the morning it ended on, so today's row is last night.</summary>
    [Fact]
    public void ASleepReadingIsDatedAsANight()
    {
        var today = new DateOnly(2026, 9, 7);
        var reply = MemberChatReplies.MetricReadingReply("Dad", StatusMetric.Sleep,
            [
                new ActivityLog { Date = today.AddDays(-1), SleepMinutes = 420 },
                new ActivityLog { Date = today, SleepMinutes = 370 },
            ], today);

        Assert.StartsWith("The most recent sleep I have for Dad is ", reply, StringComparison.Ordinal);
        Assert.Contains(", last night; the night before last it was ", reply, StringComparison.Ordinal);
    }

    /// <summary>A named reading the watch has not recorded is said to be unrecorded, by name.</summary>
    [Fact]
    public void AMetricWithNoReadingSaysSo()
    {
        var reply = MemberChatReplies.MetricReadingReply("Dad", StatusMetric.Oxygen,
            [new ActivityLog { Date = new DateOnly(2026, 9, 7), Steps = 1200 }], new DateOnly(2026, 9, 7));

        Assert.StartsWith("I don't have a recent blood oxygen reading for Dad", reply, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("how is his heart rate", StatusMetric.RestingHeartRate)]
    [InlineData("what's his pulse like", StatusMetric.RestingHeartRate)]
    [InlineData("how is his heart rate variability", StatusMetric.HeartRateVariability)]
    [InlineData("what's her HRV", StatusMetric.HeartRateVariability)]
    [InlineData("is his oxygen ok", StatusMetric.Oxygen)]
    [InlineData("how is his breathing overnight", StatusMetric.BreathingRate)]
    [InlineData("how did he sleep", StatusMetric.Sleep)]
    [InlineData("how many steps today", StatusMetric.Steps)]
    [InlineData("how is dad today", null)]
    [InlineData("his specific measurements", null)]
    public void AStatusQuestionNamesAtMostOneReading(string question, StatusMetric? expected) =>
        Assert.Equal(expected, StatusQuestion.MetricNamed(question));

    [Theory]
    [InlineData("his specific measurements", true)]
    [InlineData("her readings", true)]
    [InlineData("the numbers", true)]
    [InlineData("how is dad today", false)]
    public void AReadingsRequestIsRecognised(string question, bool expected) =>
        Assert.Equal(expected, StatusQuestion.AsksForAllReadings(question));

    // ── "How are they doing today?" ─────────────────────────────────────────────

    /// <summary>
    /// The transcript's failure, in one assertion. The caregiver asked about the day; the app
    /// answered about the instant.
    /// </summary>
    /// <remarks>
    /// The line leads, and the figures follow it. Served alone it was a dashboard caption with
    /// the dashboard taken away — "Steps are very low today." to "how is Dad today", with no
    /// number and no day, while the same question one rung up got a paragraph (2026-09-07).
    /// </remarks>
    [Fact]
    public async Task ADayQuestionIsAnsweredWithTheStatusLine_NotTheLivenessDisclaimer()
    {
        StatusLineIs("Winding down for the night.", TimeSpan.FromHours(1));
        ReadingsAre(new ActivityLog
        {
            Date = DateOnly.FromDateTime(DateTime.UtcNow),
            Steps = 4905,
            RestingHeartRate = 70,
        });

        var reply = await CreateSut().SendMessageAsync(_userId, _memberId, "How are they doing today?");

        Assert.StartsWith("Settling — Winding down for the night.", reply.Reply, StringComparison.Ordinal);
        Assert.Contains("4,905 steps", reply.Reply, StringComparison.Ordinal);
        Assert.Contains("a resting heart rate of 70 bpm", reply.Reply, StringComparison.Ordinal);
        Assert.Contains("today so far", reply.Reply, StringComparison.Ordinal);
        Assert.DoesNotContain("can't see", reply.Reply, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("right now", reply.Reply, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A fresh line with nothing recorded behind it still answers — the caption, then the honest
    /// "no recent readings" the readings fallback already says, rather than a caption alone.
    /// </summary>
    [Fact]
    public async Task AStatusLineWithNoReadingsBehindIt_StillSaysSo()
    {
        StatusLineIs("Winding down for the night.", TimeSpan.FromHours(1));
        ReadingsAre();

        var reply = await CreateSut().SendMessageAsync(_userId, _memberId, "How are they doing today?");

        Assert.StartsWith("Settling — Winding down for the night.", reply.Reply, StringComparison.Ordinal);
        Assert.Contains("don't have any recent readings for Moses", reply.Reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// The line chat serves is the row the dashboard header serves, through the same guard — which
    /// is the point: the answer was already on screen while chat disclaimed it.
    /// </summary>
    [Fact]
    public async Task AStaleLineIsNotServed_AndTheReadingsAnswerInstead()
    {
        StatusLineIs("Winding down for the night.", StatusLineStaleness.MaxAge + TimeSpan.FromHours(1));
        ReadingsAre(new ActivityLog
        {
            Date = DateOnly.FromDateTime(DateTime.UtcNow),
            Steps = 4905,
            RestingHeartRate = 70,
        });

        var reply = await CreateSut().SendMessageAsync(_userId, _memberId, "How are they doing today?");

        Assert.DoesNotContain("Winding down", reply.Reply, StringComparison.Ordinal);
        Assert.Contains("4,905 steps", reply.Reply, StringComparison.Ordinal);
        // Still not the liveness reply: a stale line is a reason to compute, not to disclaim.
        Assert.DoesNotContain("can't see", reply.Reply, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Past the ceiling this rung computes from readings rather than declining — unlike a
    /// suggestion, there is always something to say (§5).
    /// </summary>
    [Fact]
    public async Task NoLineAtAllStillAnswersFromReadings()
    {
        _statusLines.GetByCardiMemberAsync(_memberId).Returns((MemberStatusLine?)null);
        ReadingsAre(new ActivityLog
        {
            Date = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1),
            Steps = 4475,
        });

        var reply = await CreateSut().SendMessageAsync(_userId, _memberId, "How's he been today?");

        Assert.Contains("4,475 steps", reply.Reply, StringComparison.Ordinal);
        Assert.Contains("yesterday", reply.Reply, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The same member guard the dashboard reader and the batch generators apply: a paused member's
    /// stored line describes a monitoring state that no longer exists.
    /// </summary>
    [Fact]
    public async Task APausedMemberGetsNoStoredLine()
    {
        _member.MonitoringPausedUntil = DateTime.UtcNow.AddDays(7);
        StatusLineIs("Winding down for the night.", TimeSpan.FromHours(1));
        ReadingsAre(new ActivityLog { Date = DateOnly.FromDateTime(DateTime.UtcNow), Steps = 100 });

        var reply = await CreateSut().SendMessageAsync(_userId, _memberId, "How are they doing today?");

        Assert.DoesNotContain("Winding down", reply.Reply, StringComparison.Ordinal);
    }

    // ── "Is he asleep right now?" ───────────────────────────────────────────────

    /// <summary>
    /// The branch that was always right keeps working. This is the one question a caregiver is
    /// least able to check and most likely to act on, and the reply is assembled in code precisely
    /// so no model can assemble a different one.
    /// </summary>
    [Fact]
    public async Task AThisInstantQuestionStillGetsTheLivenessReply()
    {
        Triage(aboutThisMoment: true);
        StatusLineIs("Winding down for the night.", TimeSpan.FromHours(1));
        ReadingsAre(new ActivityLog { Date = DateOnly.FromDateTime(DateTime.UtcNow), Steps = 4905 });

        var reply = await CreateSut().SendMessageAsync(_userId, _memberId, "Is he asleep right now?");

        Assert.StartsWith("I can't see what Moses is doing right now", reply.Reply, StringComparison.Ordinal);
        // A fresh status line must not pre-empt it — "settling down for the night" read as an
        // answer to "is he asleep now" is exactly the false claim this branch exists to prevent.
        Assert.DoesNotContain("Winding down", reply.Reply, StringComparison.Ordinal);
    }

    /// <summary>Neither branch spends a model call beyond the triage that routed it here.</summary>
    [Fact]
    public async Task NeitherBranchPlansOrReadsClinically()
    {
        StatusLineIs("Winding down for the night.", TimeSpan.FromHours(1));

        await CreateSut().SendMessageAsync(_userId, _memberId, "How are they doing today?");

        await _planner.DidNotReceiveWithAnyArgs().PlanAsync(default!, default, default, default);
        await _medicalAi.DidNotReceiveWithAnyArgs()
            .GenerateStructuredWithUsageAsync<MemberChatService.MemberChatClinicalAiResponse>(default!, default);
    }

    // ── The reply itself ────────────────────────────────────────────────────────

    /// <summary>
    /// The readings fallback states the same figures as the liveness reply and dates them the same
    /// way — it simply does not open by refusing a question nobody asked.
    /// </summary>
    [Fact]
    public void TheReadingsReplyDatesItsFiguresWithoutDisclaiming()
    {
        var today = new DateOnly(2026, 9, 4);
        var reply = MemberChatReplies.LatestReadingsReply(
            "Dad",
            [new ActivityLog { Date = today, Steps = 4905, RestingHeartRate = 70, SleepMinutes = 333 }],
            today);

        Assert.Contains("today so far", reply, StringComparison.Ordinal);
        Assert.Contains("4,905 steps", reply, StringComparison.Ordinal);
        Assert.Contains("a resting heart rate of 70 bpm", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("can't see", reply, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The status-line reply is the caption and then the same dated figures the readings reply
    /// states — one figure list across the three status replies, so none can date a reading
    /// differently from the others.
    /// </summary>
    [Fact]
    public void TheStatusLineReplyLeadsWithTheCaption_ThenDatesTheFigures()
    {
        var today = new DateOnly(2026, 9, 7);
        var line = new MemberStatusLine
        {
            Headline = "Quieter than usual",
            Message = "Steps are very low today.",
            GeneratedAtUtc = DateTime.UtcNow,
        };

        var reply = MemberChatReplies.StatusLineReply(
            "Dad", line, [new ActivityLog { Date = today, Steps = 812, RestingHeartRate = 68 }], today);

        Assert.StartsWith("Quieter than usual — Steps are very low today.", reply, StringComparison.Ordinal);
        Assert.Contains("today so far: 812 steps and a resting heart rate of 68 bpm", reply, StringComparison.Ordinal);
    }

    /// <summary>A dropped headline — the row's documented case — leaves the sentence whole
    /// rather than a dangling dash.</summary>
    [Fact]
    public void TheStatusLineReplyReadsWholeWithoutAHeadline()
    {
        var today = new DateOnly(2026, 9, 7);
        var line = new MemberStatusLine { Headline = null, Message = "Steps are very low today." };

        var reply = MemberChatReplies.StatusLineReply("Dad", line, [], today);

        Assert.StartsWith("Steps are very low today. I don't have any recent readings", reply, StringComparison.Ordinal);
    }

    /// <summary>Nothing recorded is said plainly, and never inferred from silence.</summary>
    [Fact]
    public void TheReadingsReplySaysWhenThereIsNothing()
    {
        var reply = MemberChatReplies.LatestReadingsReply("Dad", [], new DateOnly(2026, 9, 4));

        Assert.Contains("don't have any recent readings for Dad", reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// No name means no invented relationship word — "what them is doing" is what a bare
    /// substitution produces.
    /// </summary>
    [Fact]
    public void TheReadingsReplyInventsNoNameWhenThereIsNone()
    {
        var reply = MemberChatReplies.LatestReadingsReply(null, [], new DateOnly(2026, 9, 4));

        Assert.Contains("readings for them", reply, StringComparison.Ordinal);
    }
}
