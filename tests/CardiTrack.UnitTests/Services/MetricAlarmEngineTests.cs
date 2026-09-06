using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins the engine's orchestration guarantees: an alert is written on the transition into alarm
/// and never again while the condition holds, the state row is what carries that across ticks,
/// and members whose organization has no alarms are not looked at.
/// </summary>
public class MetricAlarmEngineTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICardiMemberRepository _members = Substitute.For<ICardiMemberRepository>();
    private readonly IPatternBaselineRepository _baselines = Substitute.For<IPatternBaselineRepository>();
    private readonly IActivityLogRepository _activityLogs = Substitute.For<IActivityLogRepository>();
    private readonly IAlertRepository _alerts = Substitute.For<IAlertRepository>();
    private readonly IMetricAlarmRepository _alarms = Substitute.For<IMetricAlarmRepository>();
    private readonly IMetricAlarmStateRepository _states = Substitute.For<IMetricAlarmStateRepository>();
    private readonly IGranularMetricRepository _granular = Substitute.For<IGranularMetricRepository>();
    private readonly IDispatchService _dispatch = Substitute.For<IDispatchService>();

    private readonly Guid _memberId = Guid.NewGuid();
    private readonly Guid _organizationId = Guid.NewGuid();
    private readonly MetricAlarm _alarm;

    private static readonly DateTime UtcNow = new(2026, 9, 6, 13, 40, 0, DateTimeKind.Utc);

    public MetricAlarmEngineTests()
    {
        _alarm = new MetricAlarm
        {
            OrganizationId = _organizationId,
            Name = "Heart rate high",
            Metric = AlarmMetric.HeartRate,
            Statistic = AlarmStatistic.Average,
            Operator = AlarmOperator.GreaterThan,
            ThresholdKind = AlarmThresholdKind.Absolute,
            ThresholdValue = 120m,
            PeriodMinutes = 5,
            EvaluationPeriods = 1,
            DatapointsToAlarm = 1,
            Severity = AlertSeverity.Orange,
        };

        _unitOfWork.CardiMembers.Returns(_members);
        _unitOfWork.PatternBaselines.Returns(_baselines);
        _unitOfWork.ActivityLogs.Returns(_activityLogs);
        _unitOfWork.Alerts.Returns(_alerts);
        _unitOfWork.MetricAlarms.Returns(_alarms);
        _unitOfWork.MetricAlarmStates.Returns(_states);
        _unitOfWork.GranularMetrics.Returns(_granular);

        _alarms.GetOrganizationIdsWithEnabledAlarmsAsync(Arg.Any<CancellationToken>())
            .Returns([_organizationId]);
        _alarms.GetForMemberAsync(_organizationId, _memberId, Arg.Any<CancellationToken>())
            .Returns([_alarm]);
        _members.GetActiveIdsWithActivitySinceAsync(Arg.Any<DateOnly>()).Returns([_memberId]);
        _members.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            OrganizationId = _organizationId,
            Name = "Margaret Doe",
            IsActive = true,
        });
        _states.GetByCardiMemberAsync(_memberId, Arg.Any<CancellationToken>())
            .Returns([]);

        SetHeartRate(130);
    }

    /// <summary>A window whose last reported minutes all carry <paramref name="bpm"/>.</summary>
    private void SetHeartRate(float bpm)
    {
        var from = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
        var series = new float?[120];
        for (var i = 60; i < 100; i++)
            series[i] = bpm;

        _granular.GetWindowAsync(_memberId, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new GranularWindow
            {
                CardiMemberId = _memberId,
                FromUtc = from,
                ToUtc = from.AddMinutes(120),
                MinuteSeries = new Dictionary<GranularMetric, float?[]> { [GranularMetric.HeartRate] = series },
            });
    }

    private MetricAlarmEngine Engine() =>
        new(_unitOfWork, _dispatch, NullLogger<MetricAlarmEngine>.Instance);

    [Fact]
    public async Task RaisesOneAlert_WhenTheAlarmIsFirstBreached()
    {
        var raised = await Engine().EvaluateAsync(UtcNow);

        Assert.Equal(1, raised);
        await _alerts.Received(1).AddAsync(Arg.Is<Alert>(a =>
            a.CardiMemberId == _memberId
            && a.Severity == AlertSeverity.Orange
            && a.AlertType == AlertType.HeartRate
            && a.Title == "Heart rate high"));
    }

    [Fact]
    public async Task StampsItsOwnRuleMarker_SoTwoAlarmsCannotDedupAgainstEachOther()
    {
        Alert? written = null;
        await _alerts.AddAsync(Arg.Do<Alert>(a => written = a));

        await Engine().EvaluateAsync(UtcNow);

        // Asserted through the production predicate rather than against the JSON. AlertRuleMarkers
        // is what every producer's cooldown and dedup actually reads the marker with, and it reads
        // it as a substring — so parsing the JSON here would pass even if the stamp stopped being
        // findable by the thing that has to find it.
        Assert.NotNull(written);
        Assert.True(AlertRuleMarkers.HasRule(written!, MetricAlarmEngine.CustomRule(_alarm.Id)));
        Assert.False(AlertRuleMarkers.HasRule(written!, MetricAlarmEngine.CustomRule(Guid.NewGuid())));
    }

    [Fact]
    public async Task RaisesNothing_WhileTheAlarmIsAlreadyStanding()
    {
        // The whole point of the state row: a five-minute cron watching an hour of high heart rate
        // must page once, not twelve times.
        _states.GetByCardiMemberAsync(_memberId, Arg.Any<CancellationToken>()).Returns(
        [
            new MetricAlarmState
            {
                MetricAlarmId = _alarm.Id,
                CardiMemberId = _memberId,
                State = AlarmEvaluationState.Alarm,
                StateSinceUtc = UtcNow.AddMinutes(-30),
            },
        ]);

        var raised = await Engine().EvaluateAsync(UtcNow);

        Assert.Equal(0, raised);
        await _alerts.DidNotReceive().AddAsync(Arg.Any<Alert>());
    }

    [Fact]
    public async Task RaisesAgain_OnceTheConditionHasClearedAndReturned()
    {
        // Cleared: the reading is properly back inside the threshold.
        SetHeartRate(100);
        _states.GetByCardiMemberAsync(_memberId, Arg.Any<CancellationToken>()).Returns(
        [
            new MetricAlarmState
            {
                MetricAlarmId = _alarm.Id,
                CardiMemberId = _memberId,
                State = AlarmEvaluationState.Alarm,
                StateSinceUtc = UtcNow.AddMinutes(-30),
            },
        ]);

        Assert.Equal(0, await Engine().EvaluateAsync(UtcNow));

        // Back over the line from a cleared state — a new episode, and a new alert.
        SetHeartRate(130);
        _states.GetByCardiMemberAsync(_memberId, Arg.Any<CancellationToken>()).Returns(
        [
            new MetricAlarmState
            {
                MetricAlarmId = _alarm.Id,
                CardiMemberId = _memberId,
                State = AlarmEvaluationState.Ok,
                StateSinceUtc = UtcNow.AddMinutes(-10),
            },
        ]);

        Assert.Equal(1, await Engine().EvaluateAsync(UtcNow));
    }

    [Fact]
    public async Task RaisesNothing_WhenTheAlarmIsSwitchedOff()
    {
        _alarm.IsEnabled = false;

        Assert.Equal(0, await Engine().EvaluateAsync(UtcNow));
        await _alerts.DidNotReceive().AddAsync(Arg.Any<Alert>());
    }

    [Fact]
    public async Task RaisesNothing_WhileMonitoringIsPaused()
    {
        _members.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            OrganizationId = _organizationId,
            Name = "Margaret Doe",
            IsActive = true,
            MonitoringPausedUntil = UtcNow.AddDays(3),
        });

        Assert.Equal(0, await Engine().EvaluateAsync(UtcNow));
    }

    [Fact]
    public async Task DoesNotLookAtMembers_WhoseOrganizationHasNoAlarms()
    {
        _alarms.GetOrganizationIdsWithEnabledAlarmsAsync(Arg.Any<CancellationToken>())
            .Returns([Guid.NewGuid()]);

        Assert.Equal(0, await Engine().EvaluateAsync(UtcNow));
        await _granular.DidNotReceive().GetWindowAsync(
            Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SkipsTheWholePassWhenNobodyHasDefinedAnAlarm()
    {
        _alarms.GetOrganizationIdsWithEnabledAlarmsAsync(Arg.Any<CancellationToken>()).Returns([]);

        Assert.Equal(0, await Engine().EvaluateAsync(UtcNow));
        await _members.DidNotReceive().GetActiveIdsWithActivitySinceAsync(Arg.Any<DateOnly>());
    }

    [Fact]
    public async Task BaselineRelativeAlarm_StaysSilentWithoutAnEstablishedBaseline()
    {
        // The provisional-never-alerts principle, enforced by what the engine fetches: no 30-day
        // row means no threshold, and no threshold means insufficient data rather than a fire.
        _alarm.Metric = AlarmMetric.DailySteps;
        _alarm.Statistic = AlarmStatistic.Latest;
        _alarm.PeriodMinutes = 1440;
        _alarm.Operator = AlarmOperator.LessThan;
        _alarm.ThresholdKind = AlarmThresholdKind.BaselinePercent;
        _alarm.ThresholdValue = 70m;

        _baselines.GetLatestByCardiMemberAsync(_memberId, 30).Returns((PatternBaseline?)null);
        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns([new ActivityLog { CardiMemberId = _memberId, Date = new DateOnly(2026, 9, 5), Steps = 100 }]);

        Assert.Equal(0, await Engine().EvaluateAsync(UtcNow));
        await _alerts.DidNotReceive().AddAsync(Arg.Any<Alert>());
    }

    [Fact]
    public async Task EnqueuesTheAlertForDelivery()
    {
        await Engine().EvaluateAsync(UtcNow);

        await _dispatch.Received(1).EnqueueForAlertAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFailedDispatchDoesNotLoseTheAlert()
    {
        _dispatch.EnqueueForAlertAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("FCM is down"));

        var raised = await Engine().EvaluateAsync(UtcNow);

        Assert.Equal(1, raised);
        await _unitOfWork.Received().SaveChangesAsync();
    }
}
