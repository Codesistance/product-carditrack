using CardiTrack.Application.Services.Notifications;
using CardiTrack.Application.Services.Notifications.Rules;
using CardiTrack.Domain.Enums;
using Xunit;

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
}
