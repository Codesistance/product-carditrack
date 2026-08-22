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

    [Fact]
    public void Questionnaire_PushesImmediatelyOutsideQuietHours()
    {
        var plan = DeliveryPlanner.Plan(Context(DeliveryCategory.Questionnaire, withinQuietHours: false));

        Assert.Equal(DeliveryChannel.Push, plan.Channel);
        Assert.Null(plan.ScheduledFor);
    }

    [Fact]
    public void Questionnaire_DefersDuringQuietHours_LikeHealthOrange_NotOverridingLikeSafety()
    {
        var quietEnd = UtcNow.AddHours(3);
        var plan = DeliveryPlanner.Plan(
            Context(DeliveryCategory.Questionnaire, withinQuietHours: true, quietHoursEnd: quietEnd));

        Assert.Equal(DeliveryChannel.Push, plan.Channel);
        Assert.Equal(quietEnd, plan.ScheduledFor);
    }

    [Fact]
    public void Questionnaire_NeverEscalates()
    {
        // A question is an invitation, not an anomaly — the alert worker's own bounded reminder
        // cadence is what re-alerts an unanswered one, not the escalation ladder.
        var plan = DeliveryPlanner.Plan(Context(DeliveryCategory.Questionnaire, withinQuietHours: true));
        Assert.False(plan.Escalates);
    }

    [Fact]
    public void AllowsCritical_NeverForQuestionnaire()
    {
        Assert.False(DeliveryPlanner.AllowsCritical(DeliveryCategory.Questionnaire, severity: null));
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

    // ── Reassurance ─────────────────────────────────────────────────────────────
    //
    // The only category whose message is that nothing is wrong. Everything below is a way it
    // could borrow authority it has not earned — Health's escalation, Safety's override, the
    // critical flag — and must not.

    [Fact]
    public void Reassurance_Pushes_SoItReachesCaregiversWhoWerentAlreadyLooking()
    {
        var plan = DeliveryPlanner.Plan(Context(DeliveryCategory.Reassurance));

        Assert.Equal(DeliveryChannel.Push, plan.Channel);
        Assert.Null(plan.ScheduledFor);
    }

    [Fact]
    public void Reassurance_DefersUntilQuietHoursEnd_NeverOverridesThem()
    {
        var quietHoursEnd = UtcNow.AddHours(7);

        var plan = DeliveryPlanner.Plan(Context(
            DeliveryCategory.Reassurance, withinQuietHours: true, quietHoursEnd: quietHoursEnd));

        // Waking someone at 3am to tell them nothing is wrong is how a family learns to mute
        // this app, and the next thing muted is the alert that mattered.
        Assert.Equal(quietHoursEnd, plan.ScheduledFor);
    }

    [Fact]
    public void Reassurance_NeverEscalatesAndIsNeverCritical()
    {
        var plan = DeliveryPlanner.Plan(Context(DeliveryCategory.Reassurance));

        Assert.False(plan.Escalates);
        Assert.False(plan.AllowCritical);
        Assert.False(DeliveryPlanner.AllowsCritical(DeliveryCategory.Reassurance, severity: null));
    }

    [Fact]
    public void Reassurance_OutlivesTheOtherCategories_SoAnOvernightPhoneStillGetsIt()
    {
        var reassurance = DeliveryPlanner.Plan(Context(DeliveryCategory.Reassurance));
        var orangeHealth = DeliveryPlanner.Plan(Context(DeliveryCategory.Health, AlertSeverity.Orange));

        // "Nothing has come up" is still true hours later, unlike an anomaly teaser — and the
        // phone worth reaching with it is precisely the one that was off overnight.
        Assert.Equal(UtcNow.AddHours(6), reassurance.ExpiresAt);
        Assert.True(reassurance.ExpiresAt > orangeHealth.ExpiresAt);
    }

    [Fact]
    public void Reassurance_DeferredByQuietHours_StillGetsItsFullLifetimeFromWhenItIsSent()
    {
        var quietHoursEnd = UtcNow.AddHours(7);

        var plan = DeliveryPlanner.Plan(Context(
            DeliveryCategory.Reassurance, withinQuietHours: true, quietHoursEnd: quietHoursEnd));

        // Counted from the scheduled send, not from now — a deferred row whose TTL was measured
        // from enqueue time would expire before it was ever due.
        Assert.Equal(quietHoursEnd.AddHours(6), plan.ExpiresAt);
    }
}
