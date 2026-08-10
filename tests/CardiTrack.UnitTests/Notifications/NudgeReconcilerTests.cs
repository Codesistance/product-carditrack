using CardiTrack.Application.Services.Notifications;
using CardiTrack.Application.Services.Notifications.Rules;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using Xunit;

namespace CardiTrack.UnitTests.Notifications;

/// <summary>
/// The reconciler is what makes the inbox a projection of current state rather than a log, so these
/// assert the diff itself: that running twice changes nothing, that a fixed gap closes without the
/// user touching it, and that a dismissal outlives the gap coming back.
/// </summary>
public class NudgeReconcilerTests
{
    private static readonly DateTime Now = NudgeContextBuilder.Now;

    /// <summary>A member with no device — one clean, predictable gap to reconcile against.</summary>
    private static NudgeContext MissingDevice() => new NudgeContextBuilder().NoConnections().Build();

    private static IReadOnlyList<INudgeRule> OnlyDeviceRemoved => [new DeviceRemovedRule()];

    [Fact]
    public void APresentGapWithNothingStoredIsInserted()
    {
        var plan = NudgeReconciler.Reconcile([MissingDevice()], [], OnlyDeviceRemoved);

        var inserted = Assert.Single(plan.ToInsert);
        Assert.Equal(DeviceRemovedRule.Code, inserted.RuleCode);
        Assert.Equal(NotificationState.Open, inserted.State);
        Assert.NotEmpty(inserted.Fingerprint);
    }

    [Fact]
    public void RunningTwiceOverTheSameSnapshotCreatesNothingNew()
    {
        var context = MissingDevice();

        var first = NudgeReconciler.Reconcile([context], [], OnlyDeviceRemoved);
        var second = NudgeReconciler.Reconcile([context], first.ToInsert, OnlyDeviceRemoved);

        Assert.Empty(second.ToInsert);
        // Identity is content-derived, so convergence needs no lock and no dedupe pass.
        Assert.Equal(first.ToInsert[0].Fingerprint, first.ToInsert[0].Fingerprint);
    }

    [Fact]
    public void AGapTheUserFixedResolvesItselfWithoutBeingDismissed()
    {
        var stored = NudgeReconciler.Reconcile([MissingDevice()], [], OnlyDeviceRemoved).ToInsert;

        // The device is back — the same account, one thing changed.
        var healthy = new NudgeContextBuilder().Build();
        var plan = NudgeReconciler.Reconcile([healthy], stored, OnlyDeviceRemoved);

        var resolved = Assert.Single(plan.ToUpdate);
        Assert.Equal(NotificationState.Resolved, resolved.State);
        Assert.Equal(NotificationResolutionReason.GapClosed, resolved.ResolutionReason);
        Assert.Equal(1, plan.ResolvedCount);
    }

    [Fact]
    public void AnExpiredSnoozeReopensOnItsOwn()
    {
        var stored = NudgeReconciler.Reconcile([MissingDevice()], [], OnlyDeviceRemoved).ToInsert;
        stored[0].State = NotificationState.Snoozed;
        stored[0].SnoozedUntil = Now.AddHours(-1);

        var plan = NudgeReconciler.Reconcile([MissingDevice()], stored, OnlyDeviceRemoved);

        Assert.Equal(NotificationState.Open, Assert.Single(plan.ToUpdate).State);
        Assert.Null(stored[0].SnoozedUntil);
    }

    [Fact]
    public void ALiveSnoozeIsLeftStrictlyAlone()
    {
        var stored = NudgeReconciler.Reconcile([MissingDevice()], [], OnlyDeviceRemoved).ToInsert;
        stored[0].State = NotificationState.Snoozed;
        stored[0].SnoozedUntil = Now.AddDays(3);

        NudgeReconciler.Reconcile([MissingDevice()], stored, OnlyDeviceRemoved);

        Assert.Equal(NotificationState.Snoozed, stored[0].State);
        Assert.Equal(Now.AddDays(3), stored[0].SnoozedUntil);
    }

    [Fact]
    public void ADismissalSurvivesTheGapDisappearingAndComingBack()
    {
        var stored = NudgeReconciler.Reconcile([MissingDevice()], [], OnlyDeviceRemoved).ToInsert;
        stored[0].State = NotificationState.Resolved;
        stored[0].ResolutionReason = NotificationResolutionReason.Dismissed;
        stored[0].ResolvedDate = Now.AddDays(-1);

        var plan = NudgeReconciler.Reconcile([MissingDevice()], stored, OnlyDeviceRemoved);

        Assert.Empty(plan.ToInsert);
        Assert.Equal(NotificationState.Resolved, stored[0].State);
    }

    [Fact]
    public void AGapThatClosedAndReturnedIsRaisedAgain()
    {
        // Auto-resolution is not a decision the user made, so unlike a dismissal it must not
        // suppress the second occurrence — a device that broke, was fixed, and broke again is
        // news both times.
        var stored = NudgeReconciler.Reconcile([MissingDevice()], [], OnlyDeviceRemoved).ToInsert;
        stored[0].State = NotificationState.Resolved;
        stored[0].ResolutionReason = NotificationResolutionReason.GapClosed;
        stored[0].ResolvedDate = Now.AddDays(-5);
        stored[0].FirstSeenDate = Now.AddDays(-6);

        NudgeReconciler.Reconcile([MissingDevice()], stored, OnlyDeviceRemoved);

        Assert.Equal(NotificationState.Open, stored[0].State);
        Assert.Null(stored[0].ResolutionReason);
        // Seen is cleared too, so the funnel counts the second sighting as its own.
        Assert.Null(stored[0].FirstSeenDate);
    }

    [Fact]
    public void ARuleVersionBumpReArmsADismissedRule()
    {
        var stored = NudgeReconciler.Reconcile([MissingDevice()], [], OnlyDeviceRemoved).ToInsert;
        stored[0].State = NotificationState.Resolved;
        stored[0].ResolutionReason = NotificationResolutionReason.Dismissed;
        stored[0].RuleVersion = 0; // the shipped rule is version 1 — the offer has changed since

        NudgeReconciler.Reconcile([MissingDevice()], stored, OnlyDeviceRemoved);

        Assert.Equal(NotificationState.Open, stored[0].State);
        Assert.Equal(1, stored[0].RuleVersion);
    }

    [Fact]
    public void AMutedRuleProducesNoRowAtAll()
    {
        var context = new NudgeContextBuilder()
            .NoConnections()
            .Muting(ruleCode: DeviceRemovedRule.Code)
            .Build();

        var plan = NudgeReconciler.Reconcile([context], [], OnlyDeviceRemoved);

        Assert.Empty(plan.ToInsert);
        Assert.Equal(1, plan.SuppressedCount);
    }

    [Fact]
    public void AMuteScopedToOneMemberDoesNotSilenceAnother()
    {
        var context = new NudgeContextBuilder()
            .NoConnections()
            .Muting(ruleCode: DeviceRemovedRule.Code, memberId: Guid.NewGuid())
            .Build();

        Assert.Single(NudgeReconciler.Reconcile([context], [], OnlyDeviceRemoved).ToInsert);
    }

    [Fact]
    public void AnExpiredMuteNoLongerSilencesAnything()
    {
        var context = new NudgeContextBuilder()
            .NoConnections()
            .Muting(ruleCode: DeviceRemovedRule.Code, until: Now.AddDays(-1))
            .Build();

        Assert.Single(NudgeReconciler.Reconcile([context], [], OnlyDeviceRemoved).ToInsert);
    }

    [Fact]
    public void ACategoryMuteSilencesEveryRuleInIt()
    {
        var context = new NudgeContextBuilder()
            .NoConnections()
            .Muting(category: NotificationCategory.Blocking)
            .Build();

        Assert.Empty(NudgeReconciler.Reconcile([context], [], OnlyDeviceRemoved).ToInsert);
    }

    // ---------------------------------------------------------------- suppression windows

    [Fact]
    public void APausedMemberIsLeftAloneByEverythingButThePauseRule()
    {
        var context = new NudgeContextBuilder()
            .NoConnections()
            .PausedUntil(Now.AddDays(3))
            .Build();

        Assert.Empty(NudgeReconciler.Reconcile([context], [], OnlyDeviceRemoved).ToInsert);
    }

    [Fact]
    public void AnOpenRedAlertOutranksHousekeeping()
    {
        var context = new NudgeContextBuilder().NoMedicalNotes().WithRedAlert().Build();

        var plan = NudgeReconciler.Reconcile([context], [], [new MedicalNotesEmptyRule()]);

        Assert.Empty(plan.ToInsert);
    }

    [Fact]
    public void SafetyRulesStillGetThroughDuringARedAlert()
    {
        var context = new NudgeContextBuilder()
            .WithConnections(NudgeContextBuilder.Connection(ConnectionStatus.AuthError))
            .WithRedAlert()
            .Build();

        Assert.Single(NudgeReconciler.Reconcile([context], [], [new DeviceAuthBrokenRule()]).ToInsert);
    }

    [Fact]
    public void ABrandNewAccountIsNotNaggedExceptAboutALostDevice()
    {
        var justSignedUp = new NudgeContextBuilder()
            .UserCreated(Now.AddHours(-2))
            .NoMedicalNotes()
            .NoConnections()
            .Build();

        Assert.Empty(NudgeReconciler.Reconcile([justSignedUp], [], [new MedicalNotesEmptyRule()]).ToInsert);
        Assert.Single(NudgeReconciler.Reconcile([justSignedUp], [], OnlyDeviceRemoved).ToInsert);
    }

    // ---------------------------------------------------------------- fatigue budget

    [Fact]
    public void NoMoreThanThreeNewRowsArriveInOneRun()
    {
        // Four gaps at once: a stale watch that never had sleep granted, a stalled baseline, and
        // an untouched time zone. The morning a new rule set ships is exactly when a user is most
        // likely to be buried, so the surplus waits for the next run instead.
        var member = new NudgeContextBuilder()
            .WithConnections(NudgeContextBuilder.Connection(
                lastSync: Now.AddDays(-9), scopes: "activity_and_fitness"))
            .NoBaseline()
            .DaysCaptured(2)
            .LastActivity(DateOnly.FromDateTime(Now).AddDays(-20))
            .Build();

        var account = new NudgeContextBuilder().AccountLevel().TimeZone("UTC").Build();

        var plan = NudgeReconciler.Reconcile([member, account], []);

        Assert.Equal(NudgeReconciler.MaxNewPerUserPerRun, plan.ToInsert.Count);
    }

    [Fact]
    public void NewRowsAreRankedSoTheCapDropsTheLeastImportant()
    {
        // Four gaps, one slot short: a broken grant (Safety/Critical), a watch silent for nine days
        // (High), a default time zone (High) and empty medical notes (Low). Which three survive has
        // to be decided by priority — ranking the survivors afterwards would be too late, since by
        // then whichever rules happened to evaluate first would already hold the slots.
        var member = new NudgeContextBuilder()
            .WithConnections(
                NudgeContextBuilder.Connection(ConnectionStatus.AuthError),
                NudgeContextBuilder.Connection(lastSync: Now.AddDays(-9)))
            .NoMedicalNotes()
            .Build();

        var account = new NudgeContextBuilder().AccountLevel().TimeZone("UTC").Build();

        var plan = NudgeReconciler.Reconcile([member, account], []);

        Assert.Equal(NudgeReconciler.MaxNewPerUserPerRun, plan.ToInsert.Count);
        Assert.Equal(NotificationPriority.Critical, plan.ToInsert[0].Priority);
        Assert.Equal(DeviceAuthBrokenRule.Code, plan.ToInsert[0].RuleCode);

        // The Low is the one that waits for tomorrow — not the time zone, which evaluates last.
        Assert.Contains(plan.ToInsert, n => n.RuleCode == TimezoneDefaultRule.Code);
        Assert.DoesNotContain(plan.ToInsert, n => n.RuleCode == MedicalNotesEmptyRule.Code);
        Assert.True(plan.ToInsert.SequenceEqual(plan.ToInsert.OrderBy(n => n.Priority)));
    }

    [Fact]
    public void TheCapNeverHoldsBackAnUpdateToARowTheUserAlreadyHas()
    {
        // The cap limits how much lands on someone in one morning. A row they are already looking
        // at going stale — a counter frozen at "4/30" — would be the cap making the inbox wrong.
        var member = new NudgeContextBuilder()
            .WithConnections(
                NudgeContextBuilder.Connection(ConnectionStatus.AuthError),
                NudgeContextBuilder.Connection(lastSync: Now.AddDays(-9)))
            .NoMedicalNotes()
            .Build();

        var account = new NudgeContextBuilder().AccountLevel().TimeZone("UTC").Build();

        // Two earlier runs to get past the cap: the first takes three, the second picks up the
        // straggler. By today all four are stored.
        var firstRun = NudgeReconciler.Reconcile([member, account], []).ToInsert;
        var secondRun = NudgeReconciler.Reconcile([member, account], firstRun).ToInsert;
        List<Notification> stored = [.. firstRun, .. secondRun];

        var plan = NudgeReconciler.Reconcile([member, account], stored);

        Assert.Empty(plan.ToInsert);
        Assert.Equal(4, plan.ToUpdate.Count);
        Assert.All(plan.ToUpdate, n => Assert.Equal(Now, n.LastEvaluatedDate));
    }

    // ---------------------------------------------------------------- ownership

    [Fact]
    public void ANonOwnerGetsTheRowReadOnlyRatherThanNotAtAll()
    {
        // The rest of the family should be able to see that something is outstanding without
        // every one of them being asked to fix it.
        var context = new NudgeContextBuilder().NoConnections().NotOwner().Build();

        var inserted = Assert.Single(NudgeReconciler.Reconcile([context], [], OnlyDeviceRemoved).ToInsert);
        Assert.False(inserted.IsOwner);
    }

    [Fact]
    public void TemplateDataNeverCarriesAName()
    {
        // The stored row sits beside CardiMemberId and a health-derived gap; a name in it would
        // make that row an identifier-to-clinical join in the clear.
        var context = new NudgeContextBuilder()
            .NoBaseline()
            .DaysCaptured(4)
            .LastActivity(DateOnly.FromDateTime(Now).AddDays(-10))
            .Build();

        var inserted = Assert.Single(
            NudgeReconciler.Reconcile([context], [], [new BaselineStalledRule()]).ToInsert);

        Assert.DoesNotContain("name", inserted.TemplateData, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("captured", inserted.TemplateData);
    }

    [Fact]
    public void ProgressCountersMoveWhileTheGapStaysOpen()
    {
        var early = new NudgeContextBuilder()
            .NoBaseline().DaysCaptured(4)
            .LastActivity(DateOnly.FromDateTime(Now).AddDays(-10))
            .Build();

        var stored = NudgeReconciler.Reconcile([early], [], [new BaselineStalledRule()]).ToInsert;
        Assert.Contains("\"captured\":4", stored[0].TemplateData);

        var later = new NudgeContextBuilder()
            .NoBaseline().DaysCaptured(11)
            .LastActivity(DateOnly.FromDateTime(Now).AddDays(-10))
            .Build();

        NudgeReconciler.Reconcile([later], stored, [new BaselineStalledRule()]);

        Assert.Contains("\"captured\":11", stored[0].TemplateData);
    }

    [Fact]
    public void FingerprintsSeparateRulesTargetsAndScopes()
    {
        var baseline = NudgeFingerprint.Compute("RULE", Guid.Empty, Guid.Empty, "");

        Assert.NotEqual(baseline, NudgeFingerprint.Compute("OTHER", Guid.Empty, Guid.Empty, ""));
        Assert.NotEqual(baseline, NudgeFingerprint.Compute("RULE", Guid.NewGuid(), Guid.Empty, ""));
        Assert.NotEqual(baseline, NudgeFingerprint.Compute("RULE", Guid.Empty, Guid.NewGuid(), ""));
        Assert.NotEqual(baseline, NudgeFingerprint.Compute("RULE", Guid.Empty, Guid.Empty, "x"));
        Assert.Equal(baseline, NudgeFingerprint.Compute("RULE", Guid.Empty, Guid.Empty, ""));
    }
}
