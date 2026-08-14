using System.Text;
using System.Text.Json;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;
using CardiTrack.Infrastructure.Settings;
using Microsoft.Extensions.Caching.Distributed;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// <see cref="HealthInsightService.GetCurrentStatusMessageAsync"/> — the Dashboard hero card's
/// live status line. Distinct concerns from the other insight endpoints: it's cached (so a
/// dashboard load never pays for a fresh model call every time), and its guardrail is tone
/// rather than clinical structure.
/// </summary>
public class HealthInsightServiceStatusTests
{
    private readonly IMedicalAiService _medicalAi = Substitute.For<IMedicalAiService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IUserCardiMemberRepository _links = Substitute.For<IUserCardiMemberRepository>();
    private readonly ICardiMemberRepository _members = Substitute.For<ICardiMemberRepository>();
    private readonly IAlertRepository _alerts = Substitute.For<IAlertRepository>();
    private readonly IActivityLogRepository _activityLogs = Substitute.For<IActivityLogRepository>();
    private readonly IDistributedCache _cache = Substitute.For<IDistributedCache>();

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _outsiderId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();

    public HealthInsightServiceStatusTests()
    {
        _unitOfWork.UserCardiMembers.Returns(_links);
        _unitOfWork.CardiMembers.Returns(_members);
        _unitOfWork.Alerts.Returns(_alerts);
        _unitOfWork.ActivityLogs.Returns(_activityLogs);

        _links.GetByUserIdAsync(_userId).Returns([
            new UserCardiMember
            {
                UserId = _userId,
                CardiMemberId = _memberId,
                IsActive = true,
                CanViewHealthData = true,
            },
        ]);
        _links.GetByUserIdAsync(_outsiderId).Returns([
            new UserCardiMember
            {
                UserId = _outsiderId,
                CardiMemberId = Guid.NewGuid(),
                IsActive = true,
                CanViewHealthData = true,
            },
        ]);

        _members.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            Name = "Margaret Doe",
            DateOfBirth = new DateOnly(1948, 3, 15),
            IsActive = true,
        });
        _alerts.GetByCardiMemberAsync(_memberId, true).Returns([]);
        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns([]);
        _medicalAi.GenerateStructuredAsync<HealthInsightService.CurrentStatusAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new HealthInsightService.CurrentStatusAiResponse
            {
                Headline = "All steady",
                Message = "Margaret seems steady today.",
            });
        // NSubstitute's auto-value for an unconfigured Task<byte[]> is an empty array, not null —
        // without this, every test would read as a cache hit on an empty string.
        _cache.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((byte[]?)null);
    }

    private readonly PrivateAiSettings _aiSettings = new() { CurrentStatusBudgetSeconds = 25 };

    private HealthInsightService CreateSut() =>
        new(_medicalAi, _unitOfWork, new CardiMemberAccessService(_unitOfWork),
            PromptContextFactory.Composer(_unitOfWork), _cache, _aiSettings);

    [Fact]
    public async Task Succeeds_ForALinkedUser_AndReturnsTheModelsHeadlineAndMessage()
    {
        var result = await CreateSut().GetCurrentStatusMessageAsync(_userId, _memberId);

        Assert.Equal("All steady", result.Headline);
        Assert.Equal("Margaret seems steady today.", result.Message);
    }

    /// <summary>
    /// The headline is the punchy note the hero card leads with, so it is a label, not prose: an
    /// answer that arrived quoted or full-stopped is cleaned, and one that ran on into a sentence
    /// is dropped so the dashboard keeps its per-tier headline. The live line survives either way.
    /// </summary>
    [Theory]
    [InlineData("\"Quieter than usual.\"", "Quieter than usual")]
    [InlineData("   ", null)]
    [InlineData("Everything about today has looked broadly settled so far, which is reassuring", null)]
    public async Task HeadlineIsCleanedOrDropped_ButNeverCostsTheMessage(string headline, string? expected)
    {
        _medicalAi.GenerateStructuredAsync<HealthInsightService.CurrentStatusAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new HealthInsightService.CurrentStatusAiResponse
            {
                Headline = headline,
                Message = "Margaret seems steady today.",
            });

        var result = await CreateSut().GetCurrentStatusMessageAsync(_userId, _memberId);

        Assert.Equal(expected, result.Headline);
        Assert.Equal("Margaret seems steady today.", result.Message);
    }

    [Fact]
    public async Task Throws_ForAUserNotLinkedToTheMember()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().GetCurrentStatusMessageAsync(_outsiderId, _memberId));

        await _medicalAi.DidNotReceive().GenerateStructuredAsync<HealthInsightService.CurrentStatusAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoUnresolvedAlerts_SendsGreenAsTheSeverityTier()
    {
        await CreateSut().GetCurrentStatusMessageAsync(_userId, _memberId);

        var prompt = (string)_medicalAi.ReceivedCalls().Single().GetArguments()[0]!;
        Assert.Contains("--- Current severity tier ---", prompt);
        Assert.Contains("green", prompt);
        Assert.Contains("No unresolved alerts.", prompt);
    }

    [Fact]
    public async Task UnresolvedAlerts_SendTheWorstSeverityAndListEachAlert()
    {
        _alerts.GetByCardiMemberAsync(_memberId, true).Returns(
        [
            new Alert
            {
                CardiMemberId = _memberId,
                AlertType = AlertType.PatternBreak,
                Severity = AlertSeverity.Yellow,
                Title = "Steps below baseline",
            },
            new Alert
            {
                CardiMemberId = _memberId,
                AlertType = AlertType.Inactivity,
                Severity = AlertSeverity.Orange,
                Title = "Device has not synced",
            },
        ]);

        await CreateSut().GetCurrentStatusMessageAsync(_userId, _memberId);

        var prompt = (string)_medicalAi.ReceivedCalls().Single().GetArguments()[0]!;
        Assert.Contains("--- Current severity tier ---", prompt);
        Assert.Contains("orange", prompt);
        Assert.Contains("Steps below baseline", prompt);
        Assert.Contains("Device has not synced", prompt);
    }

    /// <summary>
    /// Resolution ends the episode without deactivating the row, so an "active" alert can be one
    /// the assessor already called over. Reading those as live had the hero line going on sounding
    /// concerned while the status colour beside it had moved on.
    /// </summary>
    [Fact]
    public async Task ResolvedAlerts_NoLongerDriveTheTier()
    {
        _alerts.GetByCardiMemberAsync(_memberId, true).Returns(
        [
            new Alert
            {
                CardiMemberId = _memberId,
                AlertType = AlertType.HeartRate,
                Severity = AlertSeverity.Red,
                Title = "Heart rate needs urgent attention",
                IsResolved = true,
            },
        ]);

        await CreateSut().GetCurrentStatusMessageAsync(_userId, _memberId);

        var prompt = (string)_medicalAi.ReceivedCalls().Single().GetArguments()[0]!;
        Assert.Contains("green", prompt);
        Assert.Contains("No unresolved alerts.", prompt);
        Assert.DoesNotContain("Heart rate needs urgent attention", prompt);
    }

    [Fact]
    public async Task TheTier_ComesFromTheWorstAlertStillUnresolved()
    {
        _alerts.GetByCardiMemberAsync(_memberId, true).Returns(
        [
            new Alert
            {
                CardiMemberId = _memberId,
                AlertType = AlertType.HeartRate,
                Severity = AlertSeverity.Red,
                Title = "Heart rate needs urgent attention",
                IsResolved = true,
            },
            new Alert
            {
                CardiMemberId = _memberId,
                AlertType = AlertType.PatternBreak,
                Severity = AlertSeverity.Yellow,
                Title = "Steps below baseline",
                IsResolved = false,
            },
        ]);

        await CreateSut().GetCurrentStatusMessageAsync(_userId, _memberId);

        var prompt = (string)_medicalAi.ReceivedCalls().Single().GetArguments()[0]!;
        Assert.Contains("yellow", prompt);
        Assert.Contains("Steps below baseline", prompt);
        Assert.DoesNotContain("Heart rate needs urgent attention", prompt);
    }

    [Fact]
    public async Task TellsTheModelToStayNonClinicalAndBrief()
    {
        await CreateSut().GetCurrentStatusMessageAsync(_userId, _memberId);

        var prompt = (string)_medicalAi.ReceivedCalls().Single().GetArguments()[0]!;
        Assert.Contains("Never use clinical terms", prompt);
        // "Never diagnose" now comes from the shared tone block every prompt opens with, rather
        // than from this prompt's own wording — same guarantee, one place.
        Assert.Contains("never diagnose", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("under 15 words", prompt);
    }

    [Fact]
    public async Task ReturnsTheCachedMessage_WithoutCallingTheModelAgain()
    {
        var cachedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var cachedJson = JsonSerializer.Serialize(
            new CachedStatus("Steady as ever", "Cached line from a minute ago.", cachedAt));
        _cache.GetAsync($"dashboard-status:{_memberId}", Arg.Any<CancellationToken>())
            .Returns(Encoding.UTF8.GetBytes(cachedJson));

        var result = await CreateSut().GetCurrentStatusMessageAsync(_userId, _memberId);

        Assert.Equal("Steady as ever", result.Headline);
        Assert.Equal("Cached line from a minute ago.", result.Message);
        // The cached generation time, not the moment this call happened to read it.
        Assert.Equal(cachedAt, result.GeneratedAt);
        await _medicalAi.DidNotReceive().GenerateStructuredAsync<HealthInsightService.CurrentStatusAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CachesAFreshMessage_ForFifteenMinutes()
    {
        await CreateSut().GetCurrentStatusMessageAsync(_userId, _memberId);

        await _cache.Received(1).SetAsync(
            $"dashboard-status:{_memberId}",
            Arg.Is<byte[]>(b =>
                JsonSerializer.Deserialize<CachedStatus>(Encoding.UTF8.GetString(b))!.Message
                    == "Margaret seems steady today."
                // The headline is cached with the line it belongs to — a cache hit that dropped it
                // would leave the hero card mismatched for the rest of the TTL.
                && JsonSerializer.Deserialize<CachedStatus>(Encoding.UTF8.GetString(b))!.Headline
                    == "All steady"),
            Arg.Is<DistributedCacheEntryOptions>(o => o.AbsoluteExpirationRelativeToNow == TimeSpan.FromMinutes(15)),
            Arg.Any<CancellationToken>());
    }

    // An empty response reads as a transient model hiccup, not a stable "nothing to say" — it
    // must not be cached, so the next call retries the model instead of being stuck for 15
    // minutes with a Message the response contract says means something different (null).
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankModelResponse_IsNeverCached_AndReturnsNullMessage(string blankResponse)
    {
        _medicalAi.GenerateStructuredAsync<HealthInsightService.CurrentStatusAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new HealthInsightService.CurrentStatusAiResponse { Message = blankResponse });

        var result = await CreateSut().GetCurrentStatusMessageAsync(_userId, _memberId);

        Assert.Null(result.Message);
        // The status key specifically — the in-flight claim is written under its own key and is
        // not a cached message.
        await _cache.DidNotReceive().SetAsync(
            $"dashboard-status:{_memberId}", Arg.Any<byte[]>(),
            Arg.Any<DistributedCacheEntryOptions>(), Arg.Any<CancellationToken>());
    }

    // ── Prompt size ─────────────────────────────────────────────────────────────
    //
    // MedGemma reads a prompt at ~68 tokens/sec on CPU, so on this endpoint — the only model call
    // a caregiver waits on — instruction length is latency, paid on nearly every call because the
    // service scales to zero and a dead instance takes its prefix cache with it.

    [Fact]
    public async Task TheRenderedPrompt_StaysWorthWaitingFor()
    {
        await CreateSut().GetCurrentStatusMessageAsync(_userId, _memberId);

        var prompt = (string)_medicalAi.ReceivedCalls().Single().GetArguments()[0]!;

        // Not the instruction budget — this is the whole thing for a typical member, and it is
        // here to catch growth arriving through the data half (extra sections, chattier labels)
        // rather than through the instructions the budget below guards.
        Assert.True(prompt.Length < 2_000,
            $"The status prompt renders to {prompt.Length} characters; every one is read back at "
            + "~68 tokens/sec while a caregiver waits.");
    }

    [Fact]
    public void TheFixedInstructions_StayWithinTheirBudget()
    {
        // A const, so this is a compile-time fact tested at runtime — which is the point: it fails
        // the moment someone adds a clarification, not the next time anyone profiles the endpoint.
        Assert.True(
            HealthInsightService.CurrentStatusInstructionsLength <= HealthInsightService.StatusPromptBudget,
            $"The status instructions are {HealthInsightService.CurrentStatusInstructionsLength} "
            + $"characters against a budget of {HealthInsightService.StatusPromptBudget}.");
    }

    // ── Not spending a model call ───────────────────────────────────────────────
    //
    // MedGemma is CPU-served and a generation measures 21–26 s, so every path that can decline to
    // start one matters more here than on the endpoints a caregiver deliberately opened. All of
    // them answer with the contract's "nothing to say yet" (a null Message), which the dashboard
    // already handles by keeping its static per-tier copy.

    private const string PendingKey = "dashboard-status-pending:";

    [Fact]
    public async Task PausedMember_IsNeverGeneratedFor()
    {
        _members.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            Name = "Margaret Doe",
            IsActive = true,
            MonitoringPausedUntil = DateTime.UtcNow.AddHours(4),
        });

        var result = await CreateSut().GetCurrentStatusMessageAsync(_userId, _memberId);

        Assert.Null(result.Message);
        await _medicalAi.DidNotReceive().GenerateStructuredAsync<HealthInsightService.CurrentStatusAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>A pause that has elapsed is not a pause — the member is being watched again.</summary>
    [Fact]
    public async Task ExpiredPause_StillGenerates()
    {
        _members.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            Name = "Margaret Doe",
            IsActive = true,
            MonitoringPausedUntil = DateTime.UtcNow.AddHours(-1),
        });

        var result = await CreateSut().GetCurrentStatusMessageAsync(_userId, _memberId);

        Assert.Equal("Margaret seems steady today.", result.Message);
    }

    [Fact]
    public async Task InactiveMember_IsNeverGeneratedFor()
    {
        _members.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            Name = "Margaret Doe",
            IsActive = false,
        });

        var result = await CreateSut().GetCurrentStatusMessageAsync(_userId, _memberId);

        Assert.Null(result.Message);
        await _medicalAi.DidNotReceive().GenerateStructuredAsync<HealthInsightService.CurrentStatusAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The cache is only written once the model answers, so without a claim every dashboard that
    /// opens during those tens of seconds starts its own generation.
    /// </summary>
    [Fact]
    public async Task GenerationAlreadyInFlight_DoesNotStartASecondOne()
    {
        _cache.GetAsync($"{PendingKey}{_memberId}", Arg.Any<CancellationToken>())
            .Returns(Encoding.UTF8.GetBytes("1"));

        var result = await CreateSut().GetCurrentStatusMessageAsync(_userId, _memberId);

        Assert.Null(result.Message);
        await _medicalAi.DidNotReceive().GenerateStructuredAsync<HealthInsightService.CurrentStatusAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>The claim expires with the budget, so a generation that dies mid-flight blocks the
    /// next attempt for that long and no longer.</summary>
    [Fact]
    public async Task ClaimsTheSlot_ForTheLengthOfTheBudget()
    {
        await CreateSut().GetCurrentStatusMessageAsync(_userId, _memberId);

        await _cache.Received(1).SetAsync(
            $"{PendingKey}{_memberId}",
            Arg.Any<byte[]>(),
            Arg.Is<DistributedCacheEntryOptions>(o =>
                o.AbsoluteExpirationRelativeToNow == TimeSpan.FromSeconds(25)),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The mobile client abandons the call at 30 s. Overrunning the budget must therefore produce
    /// an answer, not an exception that the phone never waits around to receive.
    /// </summary>
    [Fact]
    public async Task GenerationOverrunningTheBudget_AnswersWithoutALine()
    {
        _aiSettings.CurrentStatusBudgetSeconds = 1;
        _medicalAi.GenerateStructuredAsync<HealthInsightService.CurrentStatusAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
                return new HealthInsightService.CurrentStatusAiResponse { Message = string.Empty };
            });

        var result = await CreateSut().GetCurrentStatusMessageAsync(_userId, _memberId);

        Assert.Null(result.Message);
        Assert.Null(result.Headline);
        // Nothing cached, so the next dashboard load tries again rather than being stuck with the
        // overrun for the TTL window.
        await _cache.DidNotReceive().SetAsync(
            $"dashboard-status:{_memberId}", Arg.Any<byte[]>(),
            Arg.Any<DistributedCacheEntryOptions>(), Arg.Any<CancellationToken>());
    }

    /// <summary>A caller who hung up is not the same as a budget overrun, and must not be
    /// swallowed into a normal-looking answer.</summary>
    [Fact]
    public async Task CallerCancelling_StillPropagates()
    {
        using var caller = new CancellationTokenSource();
        _medicalAi.GenerateStructuredAsync<HealthInsightService.CurrentStatusAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await caller.CancelAsync();
                await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
                return new HealthInsightService.CurrentStatusAiResponse { Message = string.Empty };
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateSut().GetCurrentStatusMessageAsync(_userId, _memberId, caller.Token));
    }

    /// <summary>Structurally matches <c>HealthInsightService.CachedStatus</c> — a private nested
    /// record, so this is a separate type kept in sync by shape, not by reference.</summary>
    private sealed record CachedStatus(string? Headline, string Message, DateTimeOffset GeneratedAt);
}
