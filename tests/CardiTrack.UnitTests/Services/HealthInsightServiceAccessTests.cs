using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Services;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Insights narrate health data through a model, so an unchecked call here leaks more than raw
/// rows would. Composed with the real access service over a mocked unit of work, so these assert
/// the gate as it actually ships rather than a stub of it.
/// </summary>
public class HealthInsightServiceAccessTests
{
    private readonly IMedicalAiService _medicalAi = Substitute.For<IMedicalAiService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IUserCardiMemberRepository _links = Substitute.For<IUserCardiMemberRepository>();
    private readonly IAlertRepository _alerts = Substitute.For<IAlertRepository>();
    private readonly IActivityLogRepository _activityLogs = Substitute.For<IActivityLogRepository>();
    private readonly IPatternBaselineRepository _baselines = Substitute.For<IPatternBaselineRepository>();

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _outsiderId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();
    private readonly Guid _alertId = Guid.NewGuid();

    public HealthInsightServiceAccessTests()
    {
        _unitOfWork.UserCardiMembers.Returns(_links);
        _unitOfWork.Alerts.Returns(_alerts);
        _unitOfWork.ActivityLogs.Returns(_activityLogs);
        _unitOfWork.PatternBaselines.Returns(_baselines);

        _links.GetByUserIdAsync(_userId).Returns([
            new UserCardiMember
            {
                UserId = _userId,
                CardiMemberId = _memberId,
                IsActive = true,
                CanViewHealthData = true,
            },
        ]);
        // The outsider is a real, authenticated user — with links of their own, just not to this member.
        _links.GetByUserIdAsync(_outsiderId).Returns([
            new UserCardiMember
            {
                UserId = _outsiderId,
                CardiMemberId = Guid.NewGuid(),
                IsActive = true,
                CanViewHealthData = true,
            },
        ]);

        _alerts.GetByIdWithCardiMemberAsync(_alertId).Returns(new Alert
        {
            Id = _alertId,
            CardiMemberId = _memberId,
            Title = "Steps well below baseline",
            Message = "Steps are 91% below the 30-day baseline.",
        });
        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns([]);
        _baselines.GetLatestByCardiMemberAsync(_memberId, Arg.Any<int>()).Returns((PatternBaseline?)null);
        _medicalAi.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("Analysis body.");
    }

    private HealthInsightService CreateSut() =>
        new(_medicalAi, _unitOfWork, new CardiMemberAccessService(_unitOfWork));

    // ── AnalyzeAlertAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task AnalyzeAlert_Succeeds_ForALinkedUser()
    {
        var result = await CreateSut().AnalyzeAlertAsync(_userId, _alertId);

        Assert.Equal(_alertId, result.AlertId);
        Assert.Equal("Analysis body.", result.Explanation);
    }

    [Fact]
    public async Task AnalyzeAlert_Throws_ForAUserNotLinkedToTheAlertsMember()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().AnalyzeAlertAsync(_outsiderId, _alertId));
    }

    [Fact]
    public async Task AnalyzeAlert_SendsNothingToTheModel_WhenAccessIsRefused()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().AnalyzeAlertAsync(_outsiderId, _alertId));

        await _medicalAi.DidNotReceive().GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnalyzeAlert_ReportsForeignAlertsAndUnknownAlertsIdentically()
    {
        var sut = CreateSut();
        var unknownAlertId = Guid.NewGuid();
        _alerts.GetByIdWithCardiMemberAsync(unknownAlertId).Returns((Alert?)null);

        var foreign = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            sut.AnalyzeAlertAsync(_outsiderId, _alertId));
        var unknown = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            sut.AnalyzeAlertAsync(_outsiderId, unknownAlertId));

        // Same shape of failure either way, so the endpoint cannot be used to test alert ids
        // for existence.
        Assert.Equal(
            foreign.Message.Replace(_alertId.ToString(), "<id>"),
            unknown.Message.Replace(unknownAlertId.ToString(), "<id>"));
    }

    // ── AnalyzeBaselineAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task AnalyzeBaseline_Succeeds_ForALinkedUser()
    {
        var result = await CreateSut().AnalyzeBaselineAsync(_userId, _memberId);

        Assert.Equal(_memberId, result.CardiMemberId);
        Assert.Equal("Analysis body.", result.Summary);
    }

    [Fact]
    public async Task AnalyzeBaseline_Throws_ForAUserNotLinkedToTheMember()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().AnalyzeBaselineAsync(_outsiderId, _memberId));
    }

    [Fact]
    public async Task AnalyzeBaseline_SendsNothingToTheModel_WhenAccessIsRefused()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().AnalyzeBaselineAsync(_outsiderId, _memberId));

        await _medicalAi.DidNotReceive().GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnalyzeBaseline_Throws_WhenTheLinkForbidsHealthData()
    {
        _links.GetByUserIdAsync(_userId).Returns([
            new UserCardiMember
            {
                UserId = _userId,
                CardiMemberId = _memberId,
                IsActive = true,
                CanViewHealthData = false,
            },
        ]);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().AnalyzeBaselineAsync(_userId, _memberId));
    }
}
