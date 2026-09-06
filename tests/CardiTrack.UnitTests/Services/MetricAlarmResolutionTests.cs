using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins account-to-member inheritance. Worth its own suite because both failure directions are
/// bad in a way a caregiver would not forgive: dropping an alarm they set, or paging them from one
/// they believed they had turned off.
/// </summary>
public class MetricAlarmResolutionTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Member = Guid.NewGuid();
    private static readonly Guid OtherMember = Guid.NewGuid();

    private static MetricAlarm Alarm(
        string name, Guid? memberId = null, Guid? derivedFrom = null,
        bool enabled = true, bool active = true) => new()
    {
        OrganizationId = Org,
        CardiMemberId = memberId,
        DerivedFromAlarmId = derivedFrom,
        Name = name,
        Metric = AlarmMetric.HeartRate,
        Statistic = AlarmStatistic.Average,
        Operator = AlarmOperator.GreaterThan,
        ThresholdKind = AlarmThresholdKind.Absolute,
        ThresholdValue = 120m,
        PeriodMinutes = 5,
        EvaluationPeriods = 1,
        DatapointsToAlarm = 1,
        IsEnabled = enabled,
        IsActive = active,
    };

    [Fact]
    public void AccountDefault_IsInheritedByTheMember()
    {
        var account = Alarm("High heart rate");

        var resolved = MetricAlarmResolution.Resolve([account], Member);

        var only = Assert.Single(resolved);
        Assert.Equal(AlarmProvenance.Inherited, only.Provenance);
        Assert.Equal(account.Id, only.Alarm.Id);
    }

    [Fact]
    public void MemberOverride_ReplacesTheAccountDefault_RatherThanAddingToIt()
    {
        var account = Alarm("High heart rate");
        var tuned = Alarm("High heart rate", Member, derivedFrom: account.Id);
        tuned.ThresholdValue = 135m;

        var resolved = MetricAlarmResolution.Resolve([account, tuned], Member);

        var only = Assert.Single(resolved);
        Assert.Equal(AlarmProvenance.Overridden, only.Provenance);
        Assert.Equal(135m, only.Alarm.ThresholdValue);
        Assert.Equal(account.Id, only.Source?.Id);
    }

    [Fact]
    public void MemberOptOut_StaysInTheListButIsNotEvaluated()
    {
        // An override switched off is how a member opts out of an inherited alarm. It has to stay
        // visible — a screen that simply omitted it would look like the alarm was never set.
        var account = Alarm("High heart rate");
        var optOut = Alarm("High heart rate", Member, derivedFrom: account.Id, enabled: false);

        var resolved = MetricAlarmResolution.Resolve([account, optOut], Member);

        Assert.Single(resolved);
        Assert.False(resolved[0].Alarm.IsEnabled);
        Assert.Empty(MetricAlarmResolution.Evaluable(resolved));
    }

    [Fact]
    public void MemberOnlyAlarm_IsAnAddition()
    {
        var account = Alarm("High heart rate");
        var extra = Alarm("Blood oxygen dip", Member);

        var resolved = MetricAlarmResolution.Resolve([account, extra], Member);

        Assert.Equal(2, resolved.Count);
        Assert.Contains(resolved, r => r.Provenance == AlarmProvenance.MemberOnly && r.Alarm.Id == extra.Id);
    }

    [Fact]
    public void AnotherMembersRows_NeverResolveHere()
    {
        var theirs = Alarm("Their alarm", OtherMember);

        var resolved = MetricAlarmResolution.Resolve([theirs], Member);

        Assert.Empty(resolved);
    }

    [Fact]
    public void OverrideOfADeletedDefault_SurvivesAsTheMembersOwnAlarm()
    {
        // Deleting the account default must not silently delete everyone's tuned copy of it.
        var account = Alarm("High heart rate", active: false);
        var tuned = Alarm("High heart rate", Member, derivedFrom: account.Id);

        var resolved = MetricAlarmResolution.Resolve([account, tuned], Member);

        var only = Assert.Single(resolved);
        Assert.Equal(AlarmProvenance.MemberOnly, only.Provenance);
        Assert.Equal(tuned.Id, only.Alarm.Id);
    }

    [Fact]
    public void DeletedRows_AreNotResolvedAtAll()
    {
        var resolved = MetricAlarmResolution.Resolve([Alarm("Gone", active: false)], Member);

        Assert.Empty(resolved);
    }

    [Fact]
    public void Resolve_OrdersByNameSoTwoCallersListThemTheSameWay()
    {
        var resolved = MetricAlarmResolution.Resolve(
            [Alarm("Zebra"), Alarm("apple"), Alarm("Mango")], Member);

        Assert.Equal(["apple", "Mango", "Zebra"], resolved.Select(r => r.Alarm.Name));
    }
}
