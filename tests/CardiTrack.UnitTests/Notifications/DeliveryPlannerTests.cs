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
        DateTime? quietHoursEnd = null,
        bool isEscalation = false,
        bool escalatedPierces = false) => new()
        {
            UtcNow = UtcNow,
            Category = category,
            Severity = severity,
            DedupKey = "test:key",
            CollapseKey = null,
            IsWithinQuietHours = withinQuietHours,
            QuietHoursEndUtc = quietHoursEnd,
            IsEscalation = isEscalation,
            EscalatedAlertsPierceQuietHours = escalatedPierces
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

    // ── Escalated copies and the recipient's own quiet hours ────────────────────
    //
    // The one exception to "red and Safety always pierce". The original recipient chose to watch
    // this person; the second is being escalated TO, so their preference decides — and the
    // default is to hold. Everything below is a way that exception could leak into the rows it
    // must not touch.

    [Fact]
    public void EscalatedRed_IsHeldUntilTheRecipientsQuietHoursEnd_ByDefault()
    {
        var quietEnd = UtcNow.AddHours(4);

        var plan = DeliveryPlanner.Plan(Context(
            DeliveryCategory.Health, AlertSeverity.Red,
            withinQuietHours: true, quietHoursEnd: quietEnd, isEscalation: true));

        // Held, not dropped: the second caregiver never agreed to be woken, and waking somebody
        // who did not is how a family learns to mute the app.
        Assert.Equal(quietEnd, plan.ScheduledFor);
    }

    [Fact]
    public void EscalatedRed_PiercesQuietHours_WhenTheRecipientAskedToBeWoken()
    {
        var plan = DeliveryPlanner.Plan(Context(
            DeliveryCategory.Health, AlertSeverity.Red,
            withinQuietHours: true, quietHoursEnd: UtcNow.AddHours(4),
            isEscalation: true, escalatedPierces: true));

        // The opt-in is the whole point of the preference — a family with nobody on night cover
        // has no escalation ladder at all after dark.
        Assert.Null(plan.ScheduledFor);
    }

    [Fact]
    public void EscalatedSafety_IsAlsoHeld_NotJustHealth()
    {
        var quietEnd = UtcNow.AddHours(4);

        var plan = DeliveryPlanner.Plan(Context(
            DeliveryCategory.Safety,
            withinQuietHours: true, quietHoursEnd: quietEnd, isEscalation: true));

        // Safety overrides unconditionally for the caregiver who owns the member. An escalated
        // copy of it is still a copy, and the person receiving it still chose their own hours.
        Assert.Equal(quietEnd, plan.ScheduledFor);
    }

    [Fact]
    public void TheOriginalRed_StillPiercesQuietHours_UnaffectedByTheEscalationRule()
    {
        var plan = DeliveryPlanner.Plan(Context(
            DeliveryCategory.Health, AlertSeverity.Red,
            withinQuietHours: true, quietHoursEnd: UtcNow.AddHours(4), isEscalation: false));

        // The regression this file exists to catch: the exception must reach escalated rows only.
        Assert.Null(plan.ScheduledFor);
    }

    [Fact]
    public void EscalatedRed_OutsideQuietHours_SendsImmediatelyEitherWay()
    {
        foreach (var pierces in new[] { false, true })
        {
            var plan = DeliveryPlanner.Plan(Context(
                DeliveryCategory.Health, AlertSeverity.Red,
                withinQuietHours: false, isEscalation: true, escalatedPierces: pierces));

            // The preference answers "may this wake you", not "may this reach you".
            Assert.Null(plan.ScheduledFor);
        }
    }

    [Fact]
    public void AHeldEscalatedRed_StillEscalates_SoTheLadderKeepsItsSchedule()
    {
        var plan = DeliveryPlanner.Plan(Context(
            DeliveryCategory.Health, AlertSeverity.Red,
            withinQuietHours: true, quietHoursEnd: UtcNow.AddHours(4), isEscalation: true));

        // Holding a copy must not stall the ladder. The rung is spent on time and the alert goes
        // undelivered at t+900s if nobody answers, which is the outcome the family needs to see —
        // silently pausing the clock until 06:00 would report cover that was never there.
        Assert.True(plan.Escalates);
    }

    [Fact]
    public void AHeldEscalatedRed_GetsItsFullLifetimeFromWhenItIsActuallySent()
    {
        var quietEnd = UtcNow.AddHours(4);

        var plan = DeliveryPlanner.Plan(Context(
            DeliveryCategory.Health, AlertSeverity.Red,
            withinQuietHours: true, quietHoursEnd: quietEnd, isEscalation: true));

        // Measured from enqueue instead, a row held four hours would expire three and a half
        // hours before it was due and reach nobody at all.
        Assert.Equal(quietEnd.AddMinutes(30), plan.ExpiresAt);
    }

    [Fact]
    public void EscalationFlags_ChangeNothingForACategoryThatNeverOverrodeAnyway()
    {
        var quietEnd = UtcNow.AddHours(3);

        var held = DeliveryPlanner.Plan(Context(
            DeliveryCategory.Health, AlertSeverity.Orange,
            withinQuietHours: true, quietHoursEnd: quietEnd, isEscalation: true));
        var pierceRequested = DeliveryPlanner.Plan(Context(
            DeliveryCategory.Health, AlertSeverity.Orange,
            withinQuietHours: true, quietHoursEnd: quietEnd,
            isEscalation: true, escalatedPierces: true));

        // The opt-in grants nothing: orange defers for everybody, and an escalated orange must
        // not become the thing that wakes a household the original never would have.
        Assert.Equal(quietEnd, held.ScheduledFor);
        Assert.Equal(quietEnd, pierceRequested.ScheduledFor);
    }
}
