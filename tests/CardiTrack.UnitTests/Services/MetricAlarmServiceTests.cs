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

    /// <summary>A request that says exactly what the row says — what the list page's toggle sends.</summary>
    private static SaveMetricAlarmRequest RequestFrom(MetricAlarm alarm, bool enabled) => new()
    {
        Name = alarm.Name,
        Metric = alarm.Metric,
        Statistic = alarm.Statistic,
        Operator = alarm.Operator,
        ThresholdKind = alarm.ThresholdKind,
        ThresholdValue = alarm.ThresholdValue,
        PeriodMinutes = alarm.PeriodMinutes,
        EvaluationPeriods = alarm.EvaluationPeriods,
        DatapointsToAlarm = alarm.DatapointsToAlarm,
        MissingDataTreatment = alarm.MissingDataTreatment,
        Severity = alarm.Severity,
        ContextGate = alarm.ContextGate,
        IsEnabled = enabled,
    };

    private MetricAlarm MemberAlarm(string name)
    {
        var alarm = AccountAlarm(name);
        alarm.CardiMemberId = _memberId;
        return alarm;
    }

    [Fact]
    public async Task SwitchingAnOptedOutAlarmBackOn_PutsTheAccountDefaultBack()
    {
        // Off then on again must leave the member inheriting the default, not holding a detached
        // copy of it marked "tuned for them" that stops following account-level edits.
        var account = AccountAlarm("HR high");
        var optOut = AccountAlarm("HR high");
        optOut.CardiMemberId = _memberId;
        optOut.DerivedFromAlarmId = account.Id;
        optOut.IsEnabled = false;
        _alarms.GetForMemberAsync(_organizationId, _memberId, Arg.Any<CancellationToken>())
            .Returns([account, optOut]);

        var result = await Service().SaveMemberOverrideAsync(
            _userId, _memberId, optOut.Id, RequestFrom(optOut, enabled: true));

        Assert.Equal(account.Id, result.Id);
        Assert.Equal(AlarmProvenance.Inherited, result.Provenance);
        Assert.False(optOut.IsActive);
        await _alarms.DidNotReceive().AddAsync(Arg.Any<MetricAlarm>());
    }

    [Fact]
    public async Task SavingAnOverrideThatSaysWhatTheDefaultSays_PutsTheAccountDefaultBack()
    {
        var account = AccountAlarm("HR high");
        var tuned = AccountAlarm("HR high");
        tuned.CardiMemberId = _memberId;
        tuned.DerivedFromAlarmId = account.Id;
        tuned.ThresholdValue = 140m;
        _alarms.GetForMemberAsync(_organizationId, _memberId, Arg.Any<CancellationToken>())
            .Returns([account, tuned]);

        var result = await Service().SaveMemberOverrideAsync(
            _userId, _memberId, tuned.Id, RequestFrom(account, enabled: true));

        Assert.Equal(AlarmProvenance.Inherited, result.Provenance);
        Assert.False(tuned.IsActive);
    }

    [Fact]
    public async Task AGenuinelyDifferentOverride_StaysAnOverride()
    {
        var account = AccountAlarm("HR high");
        var optOut = AccountAlarm("HR high");
        optOut.CardiMemberId = _memberId;
        optOut.DerivedFromAlarmId = account.Id;
        optOut.IsEnabled = false;
        _alarms.GetForMemberAsync(_organizationId, _memberId, Arg.Any<CancellationToken>())
            .Returns([account, optOut]);

        var request = RequestFrom(optOut, enabled: true);
        request.ThresholdValue = 140m;

        var result = await Service().SaveMemberOverrideAsync(_userId, _memberId, optOut.Id, request);

        Assert.Equal(AlarmProvenance.Overridden, result.Provenance);
        Assert.True(optOut.IsActive);
        Assert.Equal(140m, optOut.ThresholdValue);
    }

    [Fact]
    public async Task RenamingAnAlarm_KeepsItsStandingState()
    {
        // A rename changes nothing the evaluator reads. Wiping the state would make the next tick
        // read a condition the caregiver already has the card for as a fresh transition, and page
        // again.
        var own = MemberAlarm("Oxygen");
        _alarms.GetForMemberAsync(_organizationId, _memberId, Arg.Any<CancellationToken>()).Returns([own]);

        var request = RequestFrom(own, enabled: true);
        request.Name = "Oxygen (night)";
        request.Severity = AlertSeverity.Red;
        request.ConfirmCriticalSeverity = true;

        await Service().SaveMemberOverrideAsync(_userId, _memberId, own.Id, request);

        Assert.Equal("Oxygen (night)", own.Name);
        await _states.DidNotReceive().DeleteForAlarmAsync(own.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetuningAnAlarm_ResetsItsState()
    {
        var own = MemberAlarm("Oxygen");
        _alarms.GetForMemberAsync(_organizationId, _memberId, Arg.Any<CancellationToken>()).Returns([own]);

        var request = RequestFrom(own, enabled: true);
        request.ThresholdValue = 140m;

        await Service().SaveMemberOverrideAsync(_userId, _memberId, own.Id, request);

        await _states.Received(1).DeleteForAlarmAsync(own.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SwitchingAnAlarmOff_ResetsItsState()
    {
        // A state left behind by an alarm that was off for a month is not one to trust when it
        // comes back on, so the switch resets it in both directions.
        var own = MemberAlarm("Oxygen");
        _alarms.GetForMemberAsync(_organizationId, _memberId, Arg.Any<CancellationToken>()).Returns([own]);

        await Service().SaveMemberOverrideAsync(_userId, _memberId, own.Id, RequestFrom(own, enabled: false));

        await _states.Received(1).DeleteForAlarmAsync(own.Id, Arg.Any<CancellationToken>());
    }
}
