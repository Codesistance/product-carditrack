using System.Text;
using System.Text.Json;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;
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

    private HealthInsightService CreateSut() =>
        new(_medicalAi, _unitOfWork, new CardiMemberAccessService(_unitOfWork),
            PromptContextFactory.Composer(_unitOfWork), _cache);

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
        Assert.Contains("under 12 words", prompt);
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
        await _cache.DidNotReceive().SetAsync(
            Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<DistributedCacheEntryOptions>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Structurally matches <c>HealthInsightService.CachedStatus</c> — a private nested
    /// record, so this is a separate type kept in sync by shape, not by reference.</summary>
    private sealed record CachedStatus(string? Headline, string Message, DateTimeOffset GeneratedAt);
}
