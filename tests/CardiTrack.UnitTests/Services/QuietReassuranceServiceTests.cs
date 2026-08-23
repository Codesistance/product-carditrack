using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The sweep that sends the all-clear. <c>QuietStretchTests</c> owns whether a stretch qualifies;
/// these pin the orchestration around it — that the sweep reads the same preconditions the
/// dashboard does, that the weekly pacing is derived rather than stored, and that one member's
/// failure cannot silence the rest of the estate.
/// </summary>
public class QuietReassuranceServiceTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICardiMemberRepository _members = Substitute.For<ICardiMemberRepository>();
    private readonly IPatternBaselineRepository _baselines = Substitute.For<IPatternBaselineRepository>();
    private readonly IAlertRepository _alerts = Substitute.For<IAlertRepository>();
    private readonly IDeviceConnectionRepository _connections = Substitute.For<IDeviceConnectionRepository>();
    private readonly IRealtimeAssessmentRepository _assessments = Substitute.For<IRealtimeAssessmentRepository>();
    private readonly IDispatchService _dispatch = Substitute.For<IDispatchService>();

    private readonly Guid _memberId = Guid.NewGuid();

    private static readonly DateTime UtcNow = new(2026, 8, 22, 8, 30, 0, DateTimeKind.Utc);

    public QuietReassuranceServiceTests()
    {
        _unitOfWork.CardiMembers.Returns(_members);
        _unitOfWork.PatternBaselines.Returns(_baselines);
        _unitOfWork.Alerts.Returns(_alerts);
        _unitOfWork.DeviceConnections.Returns(_connections);
        _unitOfWork.RealtimeAssessments.Returns(_assessments);

        // Defaults: one active member the pipeline is genuinely watching, quiet for eleven days.
        _members.GetActiveIdsWithActivitySinceAsync(Arg.Any<DateOnly>()).Returns([_memberId]);
        _members.GetByIdAsync(_memberId).Returns(Member());
        _baselines.GetLatestByCardiMemberAsync(_memberId, 30).Returns(new PatternBaseline
        {
            CardiMemberId = _memberId,
            PeriodDays = 30,
        });
        _alerts.GetUnresolvedByCardiMemberAsync(_memberId).Returns([]);
        _alerts.GetLastTriggeredDateAsync(_memberId, Arg.Any<CancellationToken>())
            .Returns(UtcNow.AddDays(-11));
        _connections.GetActiveByCardiMemberIdAsync(_memberId).Returns([]);
        _assessments.GetLatestAsync(_memberId, Arg.Any<CancellationToken>())
            .Returns((RealtimeAssessment?)null);
        _dispatch.EnqueueForReassuranceAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([new NotificationDelivery()]);
    }

    private CardiMember Member(DateTime? lastSync = null) => new()
    {
        Id = _memberId,
        Name = "Margaret Doe",
        DateOfBirth = new DateOnly(1948, 3, 2),
        IsActive = true,
        CreatedDate = UtcNow.AddDays(-120),
        LastSyncDate = lastSync ?? UtcNow.AddMinutes(-20),
    };

    private QuietReassuranceService CreateSut() =>
        new(_unitOfWork, _dispatch, NullLogger<QuietReassuranceService>.Instance);

    [Fact]
    public async Task EnqueuesTheAllClear_ForAMemberTheWatchHasBeenRunningOnQuietly()
    {
        var reassured = await CreateSut().SweepAsync(UtcNow);

        Assert.Equal(1, reassured);
        // Eleven quiet days is the second week — the occurrence is what paces the push, and it
        // is derived from the stretch rather than read from a stored "last reassured" column.
        await _dispatch.Received(1).EnqueueForReassuranceAsync(
            _memberId, 1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvancesTheOccurrence_AsTheStretchCrossesEachWeek()
    {
        _alerts.GetLastTriggeredDateAsync(_memberId, Arg.Any<CancellationToken>())
            .Returns(UtcNow.AddDays(-15));

        await CreateSut().SweepAsync(UtcNow);

        // A different number to last week's, so the dedup key cannot swallow this one as a repeat.
        await _dispatch.Received(1).EnqueueForReassuranceAsync(
            _memberId, 2, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendsNothing_WithoutAnEstablishedBaseline()
    {
        _baselines.GetLatestByCardiMemberAsync(_memberId, 30).Returns((PatternBaseline?)null);

        var reassured = await CreateSut().SweepAsync(UtcNow);

        // The load-bearing precondition: no baseline means no rule ran, so the silence is not
        // evidence of anything.
        Assert.Equal(0, reassured);
        await _dispatch.DidNotReceive().EnqueueForReassuranceAsync(
            Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendsNothing_WhileMonitoringIsPaused()
    {
        var member = Member();
        member.MonitoringPausedUntil = UtcNow.AddDays(2);
        _members.GetByIdAsync(_memberId).Returns(member);

        Assert.Equal(0, await CreateSut().SweepAsync(UtcNow));
        await _dispatch.DidNotReceive().EnqueueForReassuranceAsync(
            Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendsNothing_WhenTheWearableStoppedReporting()
    {
        _members.GetByIdAsync(_memberId).Returns(Member(lastSync: UtcNow.AddDays(-3)));

        Assert.Equal(0, await CreateSut().SweepAsync(UtcNow));
        await _dispatch.DidNotReceive().EnqueueForReassuranceAsync(
            Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendsNothing_WhileAnAlertIsStillOpen()
    {
        _alerts.GetUnresolvedByCardiMemberAsync(_memberId).Returns(
        [
            new Alert { CardiMemberId = _memberId, TriggeredDate = UtcNow.AddDays(-11) },
        ]);

        Assert.Equal(0, await CreateSut().SweepAsync(UtcNow));
        await _dispatch.DidNotReceive().EnqueueForReassuranceAsync(
            Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DoesNotCountAMember_WhoseFamilyHasNobodyReceivingAlerts()
    {
        _dispatch.EnqueueForReassuranceAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);

        Assert.Equal(0, await CreateSut().SweepAsync(UtcNow));
    }

    [Fact]
    public async Task OneMembersFailure_DoesNotCostTheRestOfTheEstateItsPass()
    {
        var healthyId = Guid.NewGuid();
        _members.GetActiveIdsWithActivitySinceAsync(Arg.Any<DateOnly>()).Returns([_memberId, healthyId]);
        _members.GetByIdAsync(_memberId).ThrowsAsync(new InvalidOperationException("boom"));
        _members.GetByIdAsync(healthyId).Returns(new CardiMember
        {
            Id = healthyId,
            Name = "Arthur Doe",
            DateOfBirth = new DateOnly(1950, 1, 1),
            IsActive = true,
            CreatedDate = UtcNow.AddDays(-120),
            LastSyncDate = UtcNow.AddMinutes(-20),
        });
        _baselines.GetLatestByCardiMemberAsync(healthyId, 30).Returns(new PatternBaseline
        {
            CardiMemberId = healthyId,
            PeriodDays = 30,
        });
        _alerts.GetUnresolvedByCardiMemberAsync(healthyId).Returns([]);
        _alerts.GetLastTriggeredDateAsync(healthyId, Arg.Any<CancellationToken>())
            .Returns((DateTime?)null);
        _connections.GetActiveByCardiMemberIdAsync(healthyId).Returns([]);
        _assessments.GetLatestAsync(healthyId, Arg.Any<CancellationToken>())
            .Returns((RealtimeAssessment?)null);

        Assert.Equal(1, await CreateSut().SweepAsync(UtcNow));
        await _dispatch.Received(1).EnqueueForReassuranceAsync(
            healthyId, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopsOnShutdown_RatherThanSwallowingTheCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => CreateSut().SweepAsync(UtcNow, cts.Token));
    }
}
