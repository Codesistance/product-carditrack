using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins the one place cooldown scope is decided. The heart type is shared across three producers
/// with two different re-arm contracts — the built-in and AI rules resolve their alerts, a
/// caregiver-defined alarm never does — and the predicate has to keep the second kind from
/// latching the first kind shut.
/// </summary>
public class AlertRuleMarkersTests
{
    private static Alert HeartAlert(string rule, bool resolved = false) => new()
    {
        CardiMemberId = Guid.NewGuid(),
        AlertType = AlertType.HeartRate,
        Severity = AlertSeverity.Orange,
        Title = "Heart",
        Message = "Heart",
        TriggeredDate = DateTime.UtcNow,
        IsResolved = resolved,
        MetricValues = $"{{\"rule\":\"{rule}\"}}",
    };

    private static readonly string CustomRule = MetricAlarmEngine.CustomRule(Guid.NewGuid());

    [Fact]
    public void AnUnresolvedBuiltInHeartAlert_SuppressesEveryHeartProducer()
    {
        var alert = HeartAlert(AlertRuleMarkers.RealtimeHeartRateRule);

        Assert.True(AlertRuleMarkers.Suppresses(alert, AlertType.HeartRate, "elevated_heart_rate"));
        Assert.True(AlertRuleMarkers.Suppresses(alert, AlertType.HeartRate, CustomRule));
    }

    [Fact]
    public void ACaregiverAlarmsHeartAlert_SuppressesOnlyItself()
    {
        // Its alarm may stand for days — nothing resolves the card but a person — and while it
        // does, the AI assessor and the statistical rules must still be able to page.
        var alert = HeartAlert(CustomRule);

        Assert.True(AlertRuleMarkers.IsCustomAlarm(alert));
        Assert.True(AlertRuleMarkers.Suppresses(alert, AlertType.HeartRate, CustomRule));
        Assert.False(AlertRuleMarkers.Suppresses(alert, AlertType.HeartRate, AlertRuleMarkers.RealtimeHeartRateRule));
        Assert.False(AlertRuleMarkers.Suppresses(alert, AlertType.HeartRate, "elevated_heart_rate"));
        Assert.False(AlertRuleMarkers.Suppresses(alert, AlertType.HeartRate, MetricAlarmEngine.CustomRule(Guid.NewGuid())));
    }

    [Fact]
    public void AResolvedAlert_SuppressesNothing()
    {
        Assert.False(AlertRuleMarkers.Suppresses(
            HeartAlert(AlertRuleMarkers.RealtimeHeartRateRule, resolved: true), AlertType.HeartRate, CustomRule));
        Assert.False(AlertRuleMarkers.Suppresses(
            HeartAlert(CustomRule, resolved: true), AlertType.HeartRate, CustomRule));
    }

    [Fact]
    public void ABuiltInAlert_IsNotMistakenForACustomOne()
    {
        Assert.False(AlertRuleMarkers.IsCustomAlarm(HeartAlert(AlertRuleMarkers.RealtimeHeartRateRule)));
        Assert.False(AlertRuleMarkers.IsCustomAlarm(new Alert
        {
            CardiMemberId = Guid.NewGuid(),
            AlertType = AlertType.HeartRate,
            Severity = AlertSeverity.Yellow,
            Title = "Legacy",
            Message = "Legacy",
            TriggeredDate = DateTime.UtcNow,
        }));
    }
}
