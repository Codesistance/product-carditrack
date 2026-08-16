using CardiTrack.Application.Services.Notifications;
using CardiTrack.Application.Services.Notifications.Rules;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Notifications;

/// <summary>
/// Rules are pure functions of a snapshot, so each is asserted directly at the boundary that
/// decides it — 47 hours versus 49, six sleep samples versus seven — rather than through a worker.
/// </summary>
public class NudgeRuleTests
{
    // ---------------------------------------------------------------- healthy account

    [Fact]
    public void NoRuleFiresOnAHealthyEstablishedAccount()
    {
        var memberContext = new NudgeContextBuilder().Build();
        var accountContext = new NudgeContextBuilder().AccountLevel().Build();

        foreach (var rule in NudgeRuleCatalogue.All)
        {
            Assert.False(rule.Evaluate(memberContext).HasGap,
                $"{rule.RuleCode} fired on a healthy member.");
            Assert.False(rule.Evaluate(accountContext).HasGap,
                $"{rule.RuleCode} fired on a healthy account.");
        }
    }

    // ---------------------------------------------------------------- DEVICE_AUTH_BROKEN

    [Theory]
    [InlineData(ConnectionStatus.TokenExpired, "expired")]
    [InlineData(ConnectionStatus.AuthError, "revoked")]
    public void DeviceAuthBroken_FiresWithTheVariantMatchingTheCause(ConnectionStatus status, string variant)
    {
        var context = new NudgeContextBuilder()
            .WithConnections(NudgeContextBuilder.Connection(status))
            .Build();

        var verdict = new DeviceAuthBrokenRule().Evaluate(context);

        Assert.True(verdict.HasGap);
        Assert.Equal(variant, verdict.Variant);
    }

    [Theory]
    [InlineData(ConnectionStatus.Connected)]
    [InlineData(ConnectionStatus.Disconnected)]
    [InlineData(ConnectionStatus.SyncError)]
    public void DeviceAuthBroken_IgnoresStatusesThatAreNotAnAuthFailure(ConnectionStatus status)
    {
        var context = new NudgeContextBuilder()
            .WithConnections(NudgeContextBuilder.Connection(status))
            .Build();

        Assert.False(new DeviceAuthBrokenRule().Evaluate(context).HasGap);
    }

    [Fact]
    public void DeviceAuthBroken_IsSafetyClassAndCannotBeMuted()
    {
        var spec = new DeviceAuthBrokenRule().Spec;

        Assert.Equal(NotificationCategory.Safety, spec.Category);
        Assert.False(spec.CanMute);
        Assert.Equal(TimeSpan.FromHours(72), spec.MaxSnooze);
        // "We cannot see them any more" outranks both a new account's grace period and an open
        // red alert — it is the one thing that must always get through.
        Assert.True(spec.AppliesDuringGracePeriod);
        Assert.True(spec.AppliesDuringRedAlert);
    }

    // ---------------------------------------------------------------- PUSH_UNREACHABLE

    [Fact]
    public void PushUnreachable_FiresOnTheAccountLevelContextWhenNoDeviceIsReachable()
    {
        var context = new NudgeContextBuilder().AccountLevel().PushUnreachable().Build();

        var verdict = new PushUnreachableRule().Evaluate(context);

        Assert.True(verdict.HasGap);
    }

    [Fact]
    public void PushUnreachable_DoesNotFireWhenAReachableDeviceExists()
    {
        var context = new NudgeContextBuilder().AccountLevel().Build();
        Assert.False(new PushUnreachableRule().Evaluate(context).HasGap);
    }

    [Fact]
    public void PushUnreachable_DoesNotFireOnAMemberScopedContext()
    {
        // Personal gap (§10.3) — evaluated once per user, not once per watched relative, so it
        // must stay silent on the per-member context even when unreachable is true.
        var context = new NudgeContextBuilder().PushUnreachable().Build();
        Assert.False(new PushUnreachableRule().Evaluate(context).HasGap);
    }

    [Fact]
    public void PushUnreachable_IsSafetyClassAndCannotBeMuted()
    {
        var spec = new PushUnreachableRule().Spec;
        Assert.Equal(NotificationCategory.Safety, spec.Category);
        Assert.False(spec.CanMute);
    }

    // ---------------------------------------------------------------- DEVICE_REMOVED

    [Fact]
    public void DeviceRemoved_FiresWhenAConnectionExistedAndIsGone()
    {
        var context = new NudgeContextBuilder().NoConnections().Build();
        Assert.True(new DeviceRemovedRule().Evaluate(context).HasGap);
    }

    [Fact]
    public void DeviceRemoved_StaysQuietForAMemberWhoNeverConnectedOne()
    {
        // Onboarding owns that conversation — this rule must not duplicate it.
        var context = new NudgeContextBuilder().NoConnections().NeverHadConnection().Build();
        Assert.False(new DeviceRemovedRule().Evaluate(context).HasGap);
    }

    // ---------------------------------------------------------------- DEVICE_BATTERY_LOW

    [Theory]
    [InlineData(31, false)]  // just above the loosest threshold
    [InlineData(30, true)]   // Warning threshold itself fires
    [InlineData(3, true)]
    public void DeviceBatteryLow_FiresAtTheThresholdAndNotAbove(int level, bool expected)
    {
        var context = new NudgeContextBuilder()
            .WithConnections(NudgeContextBuilder.BatteryConnection(level))
            .Build();

        Assert.Equal(expected, new DeviceBatteryLowRule().Evaluate(context).HasGap);
    }

    [Theory]
    [InlineData(30, "warning", NotificationPriority.Medium)]
    [InlineData(21, "warning", NotificationPriority.Medium)]
    [InlineData(20, "urgent", NotificationPriority.High)]
    [InlineData(11, "urgent", NotificationPriority.High)]
    [InlineData(10, "critical", NotificationPriority.Critical)]
    [InlineData(1, "critical", NotificationPriority.Critical)]
    public void DeviceBatteryLow_TierFollowsThePercentage(
        int level, string variant, NotificationPriority priority)
    {
        var context = new NudgeContextBuilder()
            .WithConnections(NudgeContextBuilder.BatteryConnection(level))
            .Build();

        var verdict = new DeviceBatteryLowRule().Evaluate(context);

        Assert.Equal(variant, verdict.Variant);
        Assert.Equal(priority, verdict.Priority);
        Assert.Equal(level, Assert.Contains("percent", verdict.TemplateData));
    }

    [Theory]
    [InlineData("Empty", "critical_empty", NotificationPriority.Critical)]
    [InlineData("Low", "urgent_unknown", NotificationPriority.High)]
    public void DeviceBatteryLow_FiresOnABandWhenNoPercentageIsReported(
        string status, string variant, NotificationPriority priority)
    {
        // batteryLevel and batteryStatus are independently optional on the provider's schema, so a
        // band with no number has to stand on its own. Empty means the device has already
        // stopped regardless of tier math, which is why it is Critical outright rather than
        // falling through to the Urgent a bare "Low" band gets.
        var context = new NudgeContextBuilder()
            .WithConnections(NudgeContextBuilder.BatteryConnection(level: null, status: status))
            .Build();

        var verdict = new DeviceBatteryLowRule().Evaluate(context);

        Assert.True(verdict.HasGap);
        Assert.Equal(variant, verdict.Variant);
        Assert.Equal(priority, verdict.Priority);
        Assert.False(verdict.TemplateData.ContainsKey("percent"),
            "A variant with no {percent} placeholder must not carry one, and vice versa.");
    }

    [Fact]
    public void DeviceBatteryLow_PrefersEmptyOverAPercentage()
    {
        // A device reporting Empty has already stopped, whatever its last number said.
        var context = new NudgeContextBuilder()
            .WithConnections(NudgeContextBuilder.BatteryConnection(4, status: "Empty"))
            .Build();

        Assert.Equal("critical_empty", new DeviceBatteryLowRule().Evaluate(context).Variant);
    }

    [Fact]
    public void DeviceBatteryLow_ReportsTheWorstTierWhenSeveralDevicesQualify()
    {
        // A Warning-tier reading must not win just because it happens to have a lower raw
        // percentage than another device's Urgent-band reading would suggest at a glance — tier
        // orders first, percentage only breaks a tie within the same tier.
        var worst = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var context = new NudgeContextBuilder()
            .WithConnections(
                NudgeContextBuilder.BatteryConnection(25),        // Warning
                NudgeContextBuilder.BatteryConnection(5, id: worst)) // Critical
            .Build();

        var verdict = new DeviceBatteryLowRule().Evaluate(context);

        Assert.Equal(worst.ToString("N"), verdict.Discriminator);
        Assert.Equal("critical", verdict.Variant);
    }

    [Theory]
    [InlineData(11, false)]  // inside the freshness window
    [InlineData(13, true)]   // aged out
    public void DeviceBatteryLow_IgnoresAReadingTooOldToMeanAnything(int hoursAgo, bool silent)
    {
        // Past the window the device stopped syncing, which DEVICE_STALE_LONG and the
        // device-silence alert own — and the percentage predates whatever charge came after.
        var context = new NudgeContextBuilder()
            .WithConnections(NudgeContextBuilder.BatteryConnection(
                5, readAt: NudgeContextBuilder.Now.AddHours(-hoursAgo)))
            .Build();

        Assert.Equal(silent, !new DeviceBatteryLowRule().Evaluate(context).HasGap);
    }

    [Fact]
    public void DeviceBatteryLow_DefersToABrokenGrant()
    {
        // Told both, a caregiver would charge a watch whose real problem is a lost grant.
        var context = new NudgeContextBuilder()
            .WithConnections(
                NudgeContextBuilder.BatteryConnection(4),
                NudgeContextBuilder.Connection(ConnectionStatus.AuthError))
            .Build();

        Assert.False(new DeviceBatteryLowRule().Evaluate(context).HasGap);
    }

    [Fact]
    public void DeviceBatteryLow_ReportsTheWorstDeviceWhenSeveralAreLow()
    {
        var worst = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var context = new NudgeContextBuilder()
            .WithConnections(
                NudgeContextBuilder.BatteryConnection(9),
                NudgeContextBuilder.BatteryConnection(2, id: worst))
            .Build();

        var verdict = new DeviceBatteryLowRule().Evaluate(context);

        Assert.Equal(worst.ToString("N"), verdict.Discriminator);
    }

    [Fact]
    public void DeviceBatteryLow_IsSafetyClassSoItCannotBeMutedAndSurvivesARedAlert()
    {
        // The delivery guarantees this rule relies on all hang off the Safety category — losing it
        // would silently demote a flat watch to an in-app row nobody sees.
        var spec = new DeviceBatteryLowRule().Spec;

        Assert.Equal(NotificationCategory.Safety, spec.Category);
        Assert.Equal(NotificationPriority.Critical, spec.Priority);
        Assert.False(spec.CanMute);
        Assert.True(spec.AppliesDuringRedAlert);
        Assert.True(DeliveryPlanner.AllowsCritical(DeliveryCategory.Safety, severity: null));
    }

    // ---------------------------------------------------------------- DEVICE_STALE_LONG

    [Theory]
    [InlineData(47, false)]
    [InlineData(48, true)]
    [InlineData(49, true)]
    public void DeviceStaleLong_TurnsOnAtExactlyFortyEightHours(int hoursSinceSync, bool expected)
    {
        var context = new NudgeContextBuilder()
            .WithConnections(NudgeContextBuilder.Connection(
                lastSync: NudgeContextBuilder.Now.AddHours(-hoursSinceSync)))
            .Build();

        Assert.Equal(expected, new DeviceStaleLongRule().Evaluate(context).HasGap);
    }

    [Fact]
    public void DeviceStaleLong_IsQuietWhenAnyOneDeviceIsStillReporting()
    {
        // Data is flowing; a second, idle watch is not a gap in what we know about the member.
        var context = new NudgeContextBuilder()
            .WithConnections(
                NudgeContextBuilder.Connection(lastSync: NudgeContextBuilder.Now.AddDays(-9)),
                NudgeContextBuilder.Connection(lastSync: NudgeContextBuilder.Now.AddMinutes(-30)))
            .Build();

        Assert.False(new DeviceStaleLongRule().Evaluate(context).HasGap);
    }

    [Fact]
    public void DeviceStaleLong_DefersToTheFasterDeviceSilenceAlert()
    {
        // InactivityDetectionWorker notices a quiet wearable within two hours and raises an alert.
        // While that stands, saying a slower version of the same thing two days later is noise.
        var context = new NudgeContextBuilder()
            .WithConnections(NudgeContextBuilder.Connection(
                lastSync: NudgeContextBuilder.Now.AddDays(-9)))
            .WithDeviceSilenceAlert()
            .Build();

        Assert.False(new DeviceStaleLongRule().Evaluate(context).HasGap);
    }

    [Fact]
    public void DeviceStaleLong_StillCoversTheCaseThatWorkerCannotSee()
    {
        // A member with no granular series never registers as silent there, so the slow rule is
        // the only thing that would ever say the watch has gone quiet.
        var context = new NudgeContextBuilder()
            .WithConnections(NudgeContextBuilder.Connection(
                lastSync: NudgeContextBuilder.Now.AddDays(-9)))
            .Build();

        Assert.True(new DeviceStaleLongRule().Evaluate(context).HasGap);
    }

    [Fact]
    public void DeviceStaleLong_CoversAConnectionStuckInSyncError()
    {
        // The grant is intact and the provider simply keeps failing. Testing for Connected alone
        // left this in a hole between here and DEVICE_AUTH_BROKEN, which only covers TokenExpired
        // and AuthError — so a watch failing for days raised nothing at all.
        var context = new NudgeContextBuilder()
            .WithConnections(NudgeContextBuilder.Connection(
                status: ConnectionStatus.SyncError,
                lastSync: NudgeContextBuilder.Now.AddDays(-9)))
            .Build();

        Assert.True(new DeviceStaleLongRule().Evaluate(context).HasGap);
    }

    [Fact]
    public void DeviceStaleLong_IsQuietWhenASyncErrorRecoveredInTime()
    {
        // A transient provider failure parks the connection in SyncError for minutes and leaves a
        // fresh LastSyncDate behind. Staleness, not status, is what this rule reports on — firing
        // on the error itself would push on every upstream blip.
        var context = new NudgeContextBuilder()
            .WithConnections(NudgeContextBuilder.Connection(
                status: ConnectionStatus.SyncError,
                lastSync: NudgeContextBuilder.Now.AddMinutes(-20)))
            .Build();

        Assert.False(new DeviceStaleLongRule().Evaluate(context).HasGap);
    }

    [Fact]
    public void DeviceStaleLong_LeavesABrokenGrantToTheLouderRule()
    {
        // Unchanged by admitting SyncError: TokenExpired and AuthError belong to
        // DEVICE_AUTH_BROKEN, and telling a caregiver to charge a watch we have lost permission to
        // read is worse than saying nothing.
        var context = new NudgeContextBuilder()
            .WithConnections(NudgeContextBuilder.Connection(
                status: ConnectionStatus.TokenExpired,
                lastSync: NudgeContextBuilder.Now.AddDays(-9)))
            .Build();

        Assert.False(new DeviceStaleLongRule().Evaluate(context).HasGap);
    }

    [Fact]
    public void DeviceStaleLong_PushesWithoutBeingSafetyClass()
    {
        // Both halves matter. The push is what reaches a caregiver whose app is closed; staying
        // Blocking is what keeps a two-day-old condition off the safety channel, which overrides
        // quiet hours and cannot be muted.
        var spec = new DeviceStaleLongRule().Spec;

        Assert.True(spec.PushesWhenOpen);
        Assert.Equal(NotificationCategory.Blocking, spec.Category);
    }

    [Fact]
    public void DeviceStaleLong_CarriesTheNeverSyncedVariantWhenNoReadingHasEverArrived()
    {
        // A connection can sit at Connected with a null LastSyncDate — no {hours} value exists to
        // substitute, so the copy must switch variant rather than leak the raw placeholder.
        var context = new NudgeContextBuilder()
            .WithConnections(NudgeContextBuilder.Connection() with { LastSyncDate = null })
            .Build();

        var verdict = new DeviceStaleLongRule().Evaluate(context);

        Assert.True(verdict.HasGap);
        Assert.Equal("never_synced", verdict.Variant);
        Assert.False(verdict.TemplateData.ContainsKey("hours"),
            "The never-synced variant's copy carries no {hours} placeholder to fill.");
    }

    [Fact]
    public void DeviceStaleLong_DefersToTheLouderAuthFailure()
    {
        // A broken grant is why nothing is arriving. Telling the caregiver to charge the watch
        // would send them after the wrong problem.
        var context = new NudgeContextBuilder()
            .WithConnections(NudgeContextBuilder.Connection(
                ConnectionStatus.AuthError, NudgeContextBuilder.Now.AddDays(-10)))
            .Build();

        Assert.False(new DeviceStaleLongRule().Evaluate(context).HasGap);
    }

    // ---------------------------------------------------------------- TIMEZONE_DEFAULT

    [Theory]
    [InlineData("UTC", true)]
    [InlineData("utc", true)]
    [InlineData("Europe/London", false)]
    [InlineData("America/New_York", false)]
    public void TimezoneDefault_FiresOnlyWhileTheColumnDefaultIsUntouched(string zone, bool expected)
    {
        var context = new NudgeContextBuilder().AccountLevel().TimeZone(zone).Build();
        Assert.Equal(expected, new TimezoneDefaultRule().Evaluate(context).HasGap);
    }

    [Fact]
    public void TimezoneDefault_IsAskedOnceOfThePersonRatherThanOncePerMember()
    {
        var memberScoped = new NudgeContextBuilder().TimeZone("UTC").Build();
        Assert.False(new TimezoneDefaultRule().Evaluate(memberScoped).HasGap);
    }

    // ---------------------------------------------------------------- BASELINE_STALLED

    [Theory]
    [InlineData(6, false)]
    [InlineData(7, true)]
    public void BaselineStalled_NeedsAFullWeekOfSilenceBeforeItCountsAsAStall(
        int daysSinceData, bool expected)
    {
        var context = new NudgeContextBuilder()
            .NoBaseline()
            .DaysCaptured(10)
            .LastActivity(DateOnly.FromDateTime(NudgeContextBuilder.Now).AddDays(-daysSinceData))
            .Build();

        Assert.Equal(expected, new BaselineStalledRule().Evaluate(context).HasGap);
    }

    [Fact]
    public void BaselineStalled_IsQuietOnceCoverageClearsTheGate()
    {
        var context = new NudgeContextBuilder()
            .NoBaseline()
            .DaysCaptured(BaselineStalledRule.DaysRequired)
            .LastActivity(DateOnly.FromDateTime(NudgeContextBuilder.Now).AddDays(-30))
            .Build();

        Assert.False(new BaselineStalledRule().Evaluate(context).HasGap);
    }

    [Fact]
    public void BaselineStalled_SaysNothingWhenNoDayHasEverArrived()
    {
        // That member's problem is the device, and a second card reading "0/30 days" on top of
        // the device card is noise rather than help.
        var context = new NudgeContextBuilder().NoBaseline().DaysCaptured(0).LastActivity(null).Build();
        Assert.False(new BaselineStalledRule().Evaluate(context).HasGap);
    }

    // ---------------------------------------------------------------- SLEEP_SCOPE_MISSING

    [Theory]
    [InlineData("sleep", false)]
    [InlineData("https://www.googleapis.com/auth/googlehealth.sleep.readonly", false)]
    [InlineData("googlehealth.sleep.readonly", false)]
    [InlineData("activity_and_fitness", true)]
    public void SleepScopeMissing_RecognisesTheGrantInEveryFormItIsStoredIn(string scope, bool expectGap)
    {
        var context = new NudgeContextBuilder()
            .WithConnections(NudgeContextBuilder.Connection(scopes: scope))
            .Build();

        Assert.Equal(expectGap, new SleepScopeMissingRule().Evaluate(context).HasGap);
    }

    [Fact]
    public void SleepScopeMissing_IsCoveredByAnyOneConnectionGrantingIt()
    {
        var context = new NudgeContextBuilder()
            .WithConnections(
                NudgeContextBuilder.Connection(scopes: "activity_and_fitness"),
                NudgeContextBuilder.Connection(scopes: "sleep"))
            .Build();

        Assert.False(new SleepScopeMissingRule().Evaluate(context).HasGap);
    }

    // ---------------------------------------------------------------- MEDICAL_NOTES_EMPTY

    [Fact]
    public void MedicalNotesEmpty_WaitsUntilThereIsABaselineToMakeUseOfThem()
    {
        var learning = new NudgeContextBuilder().NoMedicalNotes().NoBaseline().Build();
        Assert.False(new MedicalNotesEmptyRule().Evaluate(learning).HasGap);

        var established = new NudgeContextBuilder().NoMedicalNotes().Build();
        Assert.True(new MedicalNotesEmptyRule().Evaluate(established).HasGap);
    }

    // ---------------------------------------------------------------- PAUSE_LEFT_LONG

    [Theory]
    [InlineData(13, false)]
    [InlineData(14, true)]
    public void PauseLeftLong_FiresOnlyOnceAFortnightIsStillOutstanding(int daysRemaining, bool expected)
    {
        var context = new NudgeContextBuilder()
            .PausedUntil(NudgeContextBuilder.Now.AddDays(daysRemaining))
            .Build();

        Assert.Equal(expected, new PauseLeftLongRule().Evaluate(context).HasGap);
    }

    [Fact]
    public void PauseLeftLong_IsTheOnlyRuleThatEvaluatesWhilePaused()
    {
        var pauseRules = NudgeRuleCatalogue.All.Where(r => r.Spec.AppliesWhenPaused).ToList();

        Assert.Single(pauseRules);
        Assert.Equal(PauseLeftLongRule.Code, pauseRules[0].RuleCode);
    }

    // ---------------------------------------------------------------- catalogue invariants

    [Fact]
    public void EveryRuleCodeIsUniqueAndEveryRuleIsFindable()
    {
        var codes = NudgeRuleCatalogue.All.Select(r => r.RuleCode).ToList();

        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.All(codes, code => Assert.NotNull(NudgeRuleCatalogue.Find(code)));
    }

    [Fact]
    public void SafetyRulesAreExactlyTheOnesThatCannotBeMuted()
    {
        foreach (var rule in NudgeRuleCatalogue.All)
        {
            var isSafety = rule.Spec.Category == NotificationCategory.Safety;
            Assert.Equal(isSafety, !rule.Spec.CanMute);
        }
    }

    [Fact]
    public void EveryRuleDeepLinksSomewhereSpecific()
    {
        // A prompt whose action leads nowhere is worse than staying quiet, so a rule that fires
        // must always hand the client somewhere to go.
        var contexts = new[]
        {
            new NudgeContextBuilder().NoConnections().Build(),
            new NudgeContextBuilder().AccountLevel().TimeZone("UTC").Build(),
            new NudgeContextBuilder().NoMedicalNotes().Build(),
            new NudgeContextBuilder().PausedUntil(NudgeContextBuilder.Now.AddDays(20)).Build(),
            new NudgeContextBuilder()
                .WithConnections(NudgeContextBuilder.Connection(ConnectionStatus.TokenExpired))
                .Build(),
            new NudgeContextBuilder()
                .WithConnections(NudgeContextBuilder.Connection(scopes: "activity_and_fitness"))
                .Build(),
            new NudgeContextBuilder()
                .NoBaseline().DaysCaptured(3)
                .LastActivity(DateOnly.FromDateTime(NudgeContextBuilder.Now).AddDays(-20))
                .Build()
        };

        foreach (var rule in NudgeRuleCatalogue.All)
        {
            foreach (var verdict in contexts.Select(rule.Evaluate).Where(v => v.HasGap))
            {
                Assert.StartsWith("carditrack://", verdict.ActionDeepLink);
            }
        }
    }

    // ── Which rules push ──────────────────────────────────────────────────────

    [Fact]
    public void OnlyTheThreeShippedDeviceRulesPush()
    {
        // §6: "Nudges never push", with the opted-in exceptions. Pinned as an exact set rather
        // than a contains-check — a new rule quietly acquiring PushesWhenOpen is a decision that
        // should fail a test and be made on purpose, not arrive with someone's copy-paste.
        //
        // DEVICE_STALE_LONG joined deliberately. It is the only device signal for a member
        // InactivityDetectionWorker cannot see, so without it monitoring could be dark for two
        // days with nothing ever leaving the app.
        Assert.Equal(
            ["DEVICE_AUTH_BROKEN", "DEVICE_BATTERY_LOW", "DEVICE_STALE_LONG"],
            NudgeRuleCatalogue.PushableRuleCodes.Order());
    }

    [Fact]
    public void PushingIsNotTheSameDecisionAsSafetyClass()
    {
        // The two were coincident until DEVICE_STALE_LONG, and reading either off the other would
        // be wrong in both directions: PUSH_UNREACHABLE is Safety and must never push, and
        // DEVICE_STALE_LONG pushes without earning the safety channel's overridden quiet hours.
        var pushers = NudgeRuleCatalogue.All.Where(r => r.Spec.PushesWhenOpen).ToList();

        Assert.Contains(pushers, r => r.Spec.Category != NotificationCategory.Safety);
        Assert.Contains(
            NudgeRuleCatalogue.All,
            r => r.Spec.Category == NotificationCategory.Safety && !r.Spec.PushesWhenOpen);
    }

    [Fact]
    public void PushUnreachableIsSafetyClassButNeverPushes()
    {
        // The rule that exists because push is not arriving. Delivering it by push would either be
        // a contradiction or proof the gap it reports has already closed.
        var rule = NudgeRuleCatalogue.Find(PushUnreachableRule.Code);

        Assert.NotNull(rule);
        Assert.Equal(NotificationCategory.Safety, rule.Spec.Category);
        Assert.False(rule.Spec.PushesWhenOpen);
    }
}
