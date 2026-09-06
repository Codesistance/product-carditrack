using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins the ceiling on enabled alarms to what it is actually protecting — the number of alarms a
/// caregiver ends up with, not the number of rows written to get there. The distinction is easy to
/// get backwards, and getting it backwards refuses a caregiver the right to tune an alarm they
/// already have.
/// </summary>
public class MetricAlarmServiceTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IMetricAlarmRepository _alarms = Substitute.For<IMetricAlarmRepository>();
    private readonly IMetricAlarmStateRepository _states = Substitute.For<IMetricAlarmStateRepository>();
    private readonly ICardiMemberRepository _members = Substitute.For<ICardiMemberRepository>();
    private readonly ICardiMemberAccessService _access = Substitute.For<ICardiMemberAccessService>();

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();
    private readonly Guid _organizationId = Guid.NewGuid();

    public MetricAlarmServiceTests()
    {
        _unitOfWork.MetricAlarms.Returns(_alarms);
        _unitOfWork.MetricAlarmStates.Returns(_states);
        _unitOfWork.CardiMembers.Returns(_members);
        _members.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            OrganizationId = _organizationId,
            Name = "Margaret",
            IsActive = true,
        });
        _states.GetByCardiMemberAsync(_memberId, Arg.Any<CancellationToken>()).Returns([]);
    }

    private MetricAlarmService Service() => new(_unitOfWork, _access);

    private MetricAlarm AccountAlarm(string name) => new()
    {
        OrganizationId = _organizationId,
        Name = name,
        Metric = AlarmMetric.HeartRate,
        Statistic = AlarmStatistic.Average,
        Operator = AlarmOperator.GreaterThan,
        ThresholdKind = AlarmThresholdKind.Absolute,
        ThresholdValue = 120m,
        PeriodMinutes = 5,
        EvaluationPeriods = 1,
        DatapointsToAlarm = 1,
        IsEnabled = true,
    };

    private static SaveMetricAlarmRequest Request(string name) => new()
    {
        Name = name,
        Metric = AlarmMetric.HeartRate,
        Statistic = AlarmStatistic.Average,
        Operator = AlarmOperator.GreaterThan,
        ThresholdKind = AlarmThresholdKind.Absolute,
        ThresholdValue = 135m,
        PeriodMinutes = 5,
        EvaluationPeriods = 1,
        DatapointsToAlarm = 1,
        Severity = AlertSeverity.Orange,
        IsEnabled = true,
    };

    /// <summary>A full house: exactly the ceiling in enabled account-level defaults.</summary>
    private List<MetricAlarm> AtTheCeiling()
    {
        var rows = new List<MetricAlarm>();
        for (var i = 0; i < MetricAlarmValidation.MaxEnabledAlarmsPerMember; i++)
            rows.Add(AccountAlarm($"Alarm {i}"));
        return rows;
    }

    [Fact]
    public async Task OverridingAnInheritedAlarm_IsAllowedAtTheCeiling()
    {
        // The override replaces the default in this member's effective set, so the count does not
        // grow. Counting it as an addition would tell a caregiver at the ceiling that they cannot
        // tune an alarm they already have — which is not what the ceiling is for.
        var rows = AtTheCeiling();
        _alarms.GetForMemberAsync(_organizationId, _memberId, Arg.Any<CancellationToken>()).Returns(rows);

        var result = await Service().SaveMemberOverrideAsync(
            _userId, _memberId, rows[0].Id, Request("Alarm 0"));

        Assert.Equal(135m, result.ThresholdValue);
        await _alarms.Received(1).AddAsync(Arg.Is<MetricAlarm>(a =>
            a.CardiMemberId == _memberId && a.DerivedFromAlarmId == rows[0].Id));
    }

    [Fact]
    public async Task AddingAGenuinelyNewAlarm_IsRefusedAtTheCeiling()
    {
        var rows = AtTheCeiling();
        _alarms.GetForMemberAsync(_organizationId, _memberId, Arg.Any<CancellationToken>()).Returns(rows);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service().CreateMemberAlarmAsync(_userId, _memberId, Request("One more")));
    }

    [Fact]
    public async Task TurningAnAlarmOff_IsAlwaysAllowed()
    {
        // Saving something disabled can never push a member past a ceiling, so the check must not
        // stand between a caregiver and switching an alarm off.
        var rows = AtTheCeiling();
        _alarms.GetForMemberAsync(_organizationId, _memberId, Arg.Any<CancellationToken>()).Returns(rows);

        var request = Request("Alarm 0");
        request.IsEnabled = false;

        var result = await Service().SaveMemberOverrideAsync(_userId, _memberId, rows[0].Id, request);

        Assert.False(result.IsEnabled);
    }

    [Fact]
    public async Task EnablingAnAlarmThatWasOptedOutOf_CountsAsAnAdditionAndIsRefused()
    {
        // The member is at the ceiling on other alarms and has opted out of one more. Switching
        // that one back on does grow the effective count, so the ceiling applies.
        var rows = AtTheCeiling();
        var extra = AccountAlarm("Opted out");
        var optOut = AccountAlarm("Opted out");
        optOut.CardiMemberId = _memberId;
        optOut.DerivedFromAlarmId = extra.Id;
        optOut.IsEnabled = false;
        rows.Add(extra);
        rows.Add(optOut);
        _alarms.GetForMemberAsync(_organizationId, _memberId, Arg.Any<CancellationToken>()).Returns(rows);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service().SaveMemberOverrideAsync(_userId, _memberId, optOut.Id, Request("Opted out")));
    }
}
