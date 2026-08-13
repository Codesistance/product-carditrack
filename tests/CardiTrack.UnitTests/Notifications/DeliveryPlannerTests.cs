using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Notifications;

/// <summary>
/// Table tests for the §3 category table's push/quiet-hours/escalation rules, and the §7.2
/// Critical Alerts allowlist — the two places a wrong answer here means a real user either
/// doesn't get woken for an emergency, or gets woken for something that isn't one.
/// </summary>
public class DeliveryPlannerTests
{
    private static readonly DateTime UtcNow = new(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);

    private static DeliveryPlanningContext Context(
        DeliveryCategory category,
        AlertSeverity? severity = null,
        bool withinQuietHours = false,
        DateTime? quietHoursEnd = null) => new()
        {
            UtcNow = UtcNow,
            Category = category,
            Severity = severity,
            DedupKey = "test:key",
            CollapseKey = null,
            IsWithinQuietHours = withinQuietHours,
            QuietHoursEndUtc = quietHoursEnd
        };

    [Fact]
    public void Safety_AlwaysPushesAndOverridesQuietHours()
    {
        var plan = DeliveryPlanner.Plan(Context(DeliveryCategory.Safety, withinQuietHours: true));

        Assert.Equal(DeliveryChannel.Push, plan.Channel);
        Assert.Null(plan.ScheduledFor);
        Assert.True(plan.Escalates);
    }

    [Theory]
    [InlineData(AlertSeverity.Red)]
    [InlineData(AlertSeverity.Orange)]
    public void HealthRedOrOrange_Pushes(AlertSeverity severity)
    {
        var plan = DeliveryPlanner.Plan(Context(DeliveryCategory.Health, severity));
        Assert.Equal(DeliveryChannel.Push, plan.Channel);
    }

    [Theory]
    [InlineData(AlertSeverity.Yellow)]
    [InlineData(AlertSeverity.Green)]
    public void HealthYellowOrGreen_IsInAppOnly(AlertSeverity severity)
    {
        var plan = DeliveryPlanner.Plan(Context(DeliveryCategory.Health, severity));
        Assert.Equal(DeliveryChannel.InApp, plan.Channel);
    }

    [Fact]
    public void HealthRed_OverridesQuietHoursAndEscalates()
    {
        var plan = DeliveryPlanner.Plan(Context(DeliveryCategory.Health, AlertSeverity.Red, withinQuietHours: true));

        Assert.Null(plan.ScheduledFor);
        Assert.True(plan.Escalates);
    }

    [Fact]
    public void HealthOrange_DefersDuringQuietHoursAndDoesNotEscalate()
    {
        var quietEnd = UtcNow.AddHours(3);
        var plan = DeliveryPlanner.Plan(
            Context(DeliveryCategory.Health, AlertSeverity.Orange, withinQuietHours: true, quietHoursEnd: quietEnd));

        Assert.Equal(quietEnd, plan.ScheduledFor);
        Assert.False(plan.Escalates);
    }

    [Fact]
    public void HealthOrange_PushesImmediatelyOutsideQuietHours()
    {
        var plan = DeliveryPlanner.Plan(Context(DeliveryCategory.Health, AlertSeverity.Orange, withinQuietHours: false));
        Assert.Null(plan.ScheduledFor);
    }

    [Fact]
    public void Nudge_NeverPushesEvenOutsideQuietHours()
    {
        var plan = DeliveryPlanner.Plan(Context(DeliveryCategory.Nudge));
        Assert.Equal(DeliveryChannel.InApp, plan.Channel);
        Assert.False(plan.Escalates);
    }

    // ---------------------------------------------------------------- Critical Alerts allowlist (§7.2)

    [Fact]
    public void AllowsCritical_TrueForSafety()
    {
        Assert.True(DeliveryPlanner.AllowsCritical(DeliveryCategory.Safety, severity: null));
    }

    [Fact]
    public void AllowsCritical_TrueOnlyForHealthRed()
    {
        Assert.True(DeliveryPlanner.AllowsCritical(DeliveryCategory.Health, AlertSeverity.Red));
        Assert.False(DeliveryPlanner.AllowsCritical(DeliveryCategory.Health, AlertSeverity.Orange));
        Assert.False(DeliveryPlanner.AllowsCritical(DeliveryCategory.Health, AlertSeverity.Yellow));
        Assert.False(DeliveryPlanner.AllowsCritical(DeliveryCategory.Health, null));
    }

    [Fact]
    public void AllowsCritical_NeverForNudge()
    {
        Assert.False(DeliveryPlanner.AllowsCritical(DeliveryCategory.Nudge, AlertSeverity.Red));
    }
}
