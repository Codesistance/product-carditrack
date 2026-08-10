using CardiTrack.Application.Services.Alerts;
using CardiTrack.Application.Services.Alerts.Rules;
using CardiTrack.Domain.Enums;
using Xunit;

namespace CardiTrack.UnitTests.Alerts;

/// <summary>
/// The suppression rules that sit above the individual checks — the ones that decide whether a
/// caregiver hears anything at all.
/// </summary>
public class AlertGeneratorTests
{
    [Fact]
    public void AHealthyMemberProducesNothing()
    {
        Assert.Empty(AlertGenerator.Generate(new AlertContextBuilder().Build()));
    }

    [Fact]
    public void NothingAlertsWhileTheMemberIsStillBeingLearned()
    {
        // No established baseline means no "normal" to deviate from. The first thing a new
        // caregiver experiences must not be a false alarm.
        var context = new AlertContextBuilder()
            .NoBaseline()
            .LastDays(5, d => d with { Steps = 200, RestingHeartRate = 95, SleepEfficiency = 40 })
            .Build();

        Assert.Empty(AlertGenerator.Generate(context));
    }

    [Fact]
    public void NothingAlertsWhileMonitoringIsPaused()
    {
        // Alerting through a pause somebody set deliberately would make the pause a lie.
        var context = new AlertContextBuilder()
            .PausedUntil(AlertContextBuilder.Now.AddDays(3))
            .LastDays(5, d => d with { Steps = 200, RestingHeartRate = 95 })
            .Build();

        Assert.Empty(AlertGenerator.Generate(context));
    }

    [Fact]
    public void AConditionAlreadyRaisedIsNotRaisedAgain()
    {
        // Five days of the same problem is one thing happening, not five. Without this the list
        // becomes something a caregiver scrolls past.
        var context = new AlertContextBuilder()
            .AlreadyOpen(AlertType.Inactivity)
            .LastDays(2, d => d with { Steps = 500 })
            .Build();

        Assert.DoesNotContain(AlertGenerator.Generate(context), a => a.AlertType == AlertType.Inactivity);
    }

    [Fact]
    public void SeveralDistinctProblemsEachGetTheirOwnAlert()
    {
        var context = new AlertContextBuilder()
            .LastDays(3, d => d with { Steps = 500, RestingHeartRate = 85 })
            .Build();

        var raised = AlertGenerator.Generate(context);

        Assert.Contains(raised, a => a.AlertType == AlertType.Inactivity);
        Assert.Contains(raised, a => a.AlertType == AlertType.HeartRate);
    }

    [Fact]
    public void ARaisedAlertCarriesTheNumbersThatJustifiedIt()
    {
        var context = new AlertContextBuilder().LastDays(2, d => d with { Steps = 500 }).Build();

        var alert = Assert.Single(
            AlertGenerator.Generate(context, [new InactivityRule()]));

        Assert.Equal(AlertSeverity.Yellow, alert.Severity);
        Assert.False(alert.IsResolved);
        Assert.True(alert.IsActive);
        Assert.NotNull(alert.MetricValues);
        // A caregiver should be able to see why it fired rather than having to trust it.
        Assert.Contains("\"baseline\":6000", alert.MetricValues);
        Assert.Contains("500", alert.MetricValues);
    }

    [Fact]
    public void AlertCopyNeverGivesClinicalAdvice()
    {
        // CardiTrack is not a medical device. Copy may say "worth a call"; it may not diagnose or
        // recommend treatment.
        var context = new AlertContextBuilder()
            .LastDays(5, d => d with { Steps = 400, RestingHeartRate = 90, SleepEfficiency = 50 })
            .MorningHours(hoursReported: 5, stepsPerHour: 1)
            .Build();

        string[] forbidden =
            ["diagnos", "prescri", "medication", "you should take", "treat", "condition is"];

        foreach (var alert in AlertGenerator.Generate(context))
        {
            var text = $"{alert.Title} {alert.Message}".ToLowerInvariant();
            foreach (var word in forbidden)
                Assert.DoesNotContain(word, text);
        }
    }

    [Fact]
    public void EveryRuleHasADistinctTypeAndAPlausibleSeverity()
    {
        var types = AlertGenerator.Rules.Select(r => r.AlertType).ToList();
        Assert.Equal(types.Count, types.Distinct().Count());
        Assert.Equal(5, types.Count);

        // Red is reserved for the one rule that means "go and check on them".
        var red = AlertGenerator.Rules.Where(r => r.Severity == AlertSeverity.Red).ToList();
        Assert.Single(red);
        Assert.Equal(AlertType.PatternBreak, red[0].AlertType);

        // Green is a health status, not an alert severity — no rule may emit one.
        Assert.DoesNotContain(AlertGenerator.Rules, r => r.Severity == AlertSeverity.Green);
    }
}
