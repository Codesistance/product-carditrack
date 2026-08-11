using System.Text;
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
        _medicalAi.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("Margaret seems steady today.");
        // NSubstitute's auto-value for an unconfigured Task<byte[]> is an empty array, not null —
        // without this, every test would read as a cache hit on an empty string.
        _cache.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((byte[]?)null);
    }

    private HealthInsightService CreateSut() =>
        new(_medicalAi, _unitOfWork, new CardiMemberAccessService(_unitOfWork), _cache);

    [Fact]
    public async Task Succeeds_ForALinkedUser_AndReturnsTheModelsMessage()
    {
        var result = await CreateSut().GetCurrentStatusMessageAsync(_userId, _memberId);

        Assert.Equal("Margaret seems steady today.", result.Message);
    }

    [Fact]
    public async Task Throws_ForAUserNotLinkedToTheMember()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().GetCurrentStatusMessageAsync(_outsiderId, _memberId));

        await _medicalAi.DidNotReceive().GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
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

    [Fact]
    public async Task TellsTheModelToStayNonClinicalAndBrief()
    {
        await CreateSut().GetCurrentStatusMessageAsync(_userId, _memberId);

        var prompt = (string)_medicalAi.ReceivedCalls().Single().GetArguments()[0]!;
        Assert.Contains("Never use clinical terms", prompt);
        Assert.Contains("never diagnose", prompt);
        Assert.Contains("under 12 words", prompt);
    }

    [Fact]
    public async Task ReturnsTheCachedMessage_WithoutCallingTheModelAgain()
    {
        _cache.GetAsync($"dashboard-status:{_memberId}", Arg.Any<CancellationToken>())
            .Returns(Encoding.UTF8.GetBytes("Cached line from a minute ago."));

        var result = await CreateSut().GetCurrentStatusMessageAsync(_userId, _memberId);

        Assert.Equal("Cached line from a minute ago.", result.Message);
        await _medicalAi.DidNotReceive().GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CachesAFreshMessage_ForFifteenMinutes()
    {
        await CreateSut().GetCurrentStatusMessageAsync(_userId, _memberId);

        await _cache.Received(1).SetAsync(
            $"dashboard-status:{_memberId}",
            Arg.Is<byte[]>(b => Encoding.UTF8.GetString(b) == "Margaret seems steady today."),
            Arg.Is<DistributedCacheEntryOptions>(o => o.AbsoluteExpirationRelativeToNow == TimeSpan.FromMinutes(15)),
            Arg.Any<CancellationToken>());
    }
}
