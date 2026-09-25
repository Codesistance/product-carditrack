using CardiTrack.Application.Services.Notifications;
using CardiTrack.Application.Services.Notifications.Rules;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Notifications;

/// <summary>
/// The per-member setup checklist is the setup nudges read as "done or not" rather than "ask now or
/// not". Asserted against the same <see cref="NudgeContextBuilder"/> the rule tests use, whose
/// default is a healthy established member — so every test changes the one thing it is about.
/// </summary>
public class MemberSetupChecklistTests
{
    private static readonly Guid MemberId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ConnectionId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static IReadOnlyList<MemberSetupChecklist.StepResult> Evaluate(NudgeContextBuilder builder) =>
        MemberSetupChecklist.Evaluate(builder.Build());

    private static MemberSetupChecklist.StepResult StepOf(
        IReadOnlyList<MemberSetupChecklist.StepResult> results, string key) =>
        Assert.Single(results, r => r.Step.Key == key);

    private static string[] Keys(IReadOnlyList<MemberSetupChecklist.StepResult> results) =>
        [.. results.Select(r => r.Step.Key)];

    // ---------------------------------------------------------------- shape

    [Fact]
    public void AHealthyMemberWithADevice_HasEveryApplicableStepDone()
    {
        var results = Evaluate(new NudgeContextBuilder());

        // No IRN step: the default device does not grant the rhythm scope, which is every device today.
        Assert.Equal(new[] { "emergency-contact", "sleep-access", "time-zone", "medical-information" }, Keys(results));
        Assert.All(results, r => Assert.True(r.Done, $"{r.Step.Key} should be done."));
    }

    [Fact]
    public void StepsFollowTheNudgesPriority_WithMedicalInformationLast()
    {
        Assert.Equal(new[] { "emergency-contact", "sleep-access", "irregular-rhythm", "time-zone", "medical-information" },
            MemberSetupChecklist.Steps.Select(s => s.Key));

        Assert.Equal(
            MemberSetupChecklist.Steps.Select(s => s.Priority).Order(),
            MemberSetupChecklist.Steps.Select(s => s.Priority));
    }

    /// <summary>
    /// The steps come out of the catalogue rather than restating it: every rule a step names is the
    /// catalogue's own instance, so a copy-changed rule cannot be half-updated here.
    /// </summary>
    [Fact]
    public void EveryStepRuleIsTheCataloguesOwnInstance()
    {
        foreach (var rule in MemberSetupChecklist.Steps.SelectMany(s => s.Rules))
            Assert.Contains(rule, NudgeRuleCatalogue.All);
    }

    [Fact]
    public void AnAccountLevelContextIsRefused()
    {
        Assert.Throws<ArgumentException>(
            () => MemberSetupChecklist.Evaluate(new NudgeContextBuilder().AccountLevel().Build()));
    }

    // ---------------------------------------------------------------- applicability

    [Fact]
    public void WithNoDevice_SleepAndRhythmAreLeftOut_NotCountedDone()
    {
        var results = Evaluate(new NudgeContextBuilder().NoConnections());

        Assert.Equal(new[] { "emergency-contact", "time-zone", "medical-information" }, Keys(results));
    }

    [Theory]
    [InlineData(ConnectionStatus.Disconnected)]
    [InlineData(ConnectionStatus.TokenExpired)]
    public void ADeviceThatIsNotConnected_CannotGrantSleep_SoTheStepIsLeftOut(ConnectionStatus status)
    {
        var results = Evaluate(new NudgeContextBuilder()
            .WithConnections(NudgeContextBuilder.Connection(status)));

        Assert.DoesNotContain("sleep-access", Keys(results));
    }

    [Fact]
    public void AConnectedDeviceWithoutSleep_LeavesTheStepOpen_LinkingToThatDevice()
    {
        var step = StepOf(
            Evaluate(new NudgeContextBuilder().WithConnections(
                NudgeContextBuilder.Connection(ConnectionStatus.Connected, null, "activity_and_fitness"))),
            "sleep-access");

        Assert.False(step.Done);
        Assert.Equal($"carditrack://cardimembers/{MemberId}/devices/{ConnectionId}", step.ActionDeepLink);
    }

    [Fact]
    public void ADoneSleepStep_StillCarriesTheDeviceLink()
    {
        var step = StepOf(Evaluate(new NudgeContextBuilder()), "sleep-access");

        Assert.True(step.Done);
        Assert.Equal($"carditrack://cardimembers/{MemberId}/devices/{ConnectionId}", step.ActionDeepLink);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void RhythmChecks_AreAStepOnceADeviceCanAnswer(bool enrolled, bool expectedDone)
    {
        var step = StepOf(
            Evaluate(new NudgeContextBuilder().WithConnections(NudgeContextBuilder.IrnConnection(enrolled))),
            "irregular-rhythm");

        Assert.Equal(expectedDone, step.Done);
        Assert.Equal($"carditrack://cardimembers/{MemberId}/devices/{ConnectionId}", step.ActionDeepLink);
    }

    /// <summary>
    /// A scoped device whose profile read has not landed is "unknown". The nudge will not call that a
    /// gap; the checklist will not call it done either — so it is not a step at all.
    /// </summary>
    [Fact]
    public void RhythmChecks_WithOnlyAnUnknownAnswer_AreLeftOut()
    {
        var results = Evaluate(new NudgeContextBuilder()
            .WithConnections(NudgeContextBuilder.IrnConnection(enrolled: null)));

        Assert.DoesNotContain("irregular-rhythm", Keys(results));
    }

    [Fact]
    public void RhythmChecks_OnADeviceThatNoLongerGrantsTheScope_AreLeftOut()
    {
        var results = Evaluate(new NudgeContextBuilder()
            .WithConnections(NudgeContextBuilder.IrnConnection(enrolled: false, grantsIrnScope: false)));

        Assert.DoesNotContain("irregular-rhythm", Keys(results));
    }

    // ---------------------------------------------------------------- done / not done

    [Fact]
    public void AMissingEmergencyContact_IsNotDone_AndLinksToTheField()
    {
        var step = StepOf(Evaluate(new NudgeContextBuilder().NoEmergencyContact()), "emergency-contact");

        Assert.False(step.Done);
        Assert.Equal($"carditrack://cardimembers/{MemberId}/edit#emergencyContact", step.ActionDeepLink);
    }

    /// <summary>
    /// The nudge waits a day before asking about a brand-new member. That is a "not yet" for the
    /// nudge; the member still has no number for SOS, so the step is not done.
    /// </summary>
    [Fact]
    public void ANewMembersMissingContact_IsNotDone_EvenInsideTheNudgesGrace()
    {
        var context = new NudgeContextBuilder()
            .NoEmergencyContact()
            .MemberCreated(NudgeContextBuilder.Now.AddHours(-2))
            .Build();

        Assert.False(new EmergencyContactMissingRule().Evaluate(context).HasGap);
        Assert.False(StepOf(MemberSetupChecklist.Evaluate(context), "emergency-contact").Done);
    }

    [Fact]
    public void AnAccountStillOnUtc_LeavesTheTimeZoneStepOpen_OnEveryMember()
    {
        var step = StepOf(Evaluate(new NudgeContextBuilder().TimeZone("UTC")), "time-zone");

        Assert.False(step.Done);
        Assert.Equal("carditrack://settings/profile#timezone", step.ActionDeepLink);
    }

    /// <summary>
    /// Pausing a member, or a red alert, silences the nudges — that is timing. It must not tick the
    /// checklist to complete.
    /// </summary>
    [Fact]
    public void APausedMemberOrAnOpenRedAlert_DoesNotCompleteAnything()
    {
        var paused = Evaluate(new NudgeContextBuilder()
            .NoEmergencyContact()
            .PausedUntil(NudgeContextBuilder.Now.AddDays(3)));
        var redAlert = Evaluate(new NudgeContextBuilder().NoEmergencyContact().WithRedAlert());

        Assert.False(StepOf(paused, "emergency-contact").Done);
        Assert.False(StepOf(redAlert, "emergency-contact").Done);
    }

    // ---------------------------------------------------------------- medical: two rules, one step

    [Fact]
    public void NoMedicalNotes_IsOneOpenStep()
    {
        var results = Evaluate(new NudgeContextBuilder().NoMedicalNotes());

        var step = StepOf(results, "medical-information");
        Assert.False(step.Done);
        Assert.Equal($"carditrack://cardimembers/{MemberId}/edit#medicalNotes", step.ActionDeepLink);
    }

    [Fact]
    public void StaleMedicalNotes_AreTheSameOneStep_StillOpen()
    {
        var results = Evaluate(new NudgeContextBuilder()
            .NotesReviewedAt(NudgeContextBuilder.Now.AddDays(-200)));

        Assert.False(StepOf(results, "medical-information").Done);
    }

    [Fact]
    public void RecentlyConfirmedMedicalNotes_CloseTheStep()
    {
        var results = Evaluate(new NudgeContextBuilder()
            .NotesReviewedAt(NudgeContextBuilder.Now.AddDays(-10)));

        Assert.True(StepOf(results, "medical-information").Done);
    }

    /// <summary>
    /// Both medical nudges wait for a baseline before they ask. Missing notes are still missing.
    /// </summary>
    [Fact]
    public void MissingNotes_AreNotDone_BeforeThereIsABaseline()
    {
        var context = new NudgeContextBuilder().NoMedicalNotes().NoBaseline().Build();

        Assert.False(new MedicalNotesEmptyRule().Evaluate(context).HasGap);
        Assert.False(StepOf(MemberSetupChecklist.Evaluate(context), "medical-information").Done);
    }

    // ---------------------------------------------------------------- mutes

    [Fact]
    public void AMutedRule_TakesItsStepOffTheTotal()
    {
        var results = Evaluate(new NudgeContextBuilder()
            .NoEmergencyContact()
            .Muting(ruleCode: EmergencyContactMissingRule.Code));

        Assert.DoesNotContain("emergency-contact", Keys(results));
    }

    [Fact]
    public void AMuteForThisMemberOnly_TakesTheStepOff()
    {
        var results = Evaluate(new NudgeContextBuilder()
            .Muting(ruleCode: EmergencyContactMissingRule.Code, memberId: MemberId));

        Assert.DoesNotContain("emergency-contact", Keys(results));
    }

    [Fact]
    public void AMuteForADifferentMember_LeavesThisMembersStep()
    {
        var results = Evaluate(new NudgeContextBuilder()
            .Muting(ruleCode: EmergencyContactMissingRule.Code, memberId: Guid.NewGuid()));

        Assert.Contains("emergency-contact", Keys(results));
    }

    [Fact]
    public void AnExpiredMute_NoLongerTakesTheStepOff()
    {
        var results = Evaluate(new NudgeContextBuilder()
            .Muting(ruleCode: EmergencyContactMissingRule.Code, until: NudgeContextBuilder.Now.AddMinutes(-1)));

        Assert.Contains("emergency-contact", Keys(results));
    }

    [Fact]
    public void MutingTheUnlockCategory_LeavesOnlyTheTimeZone()
    {
        var results = Evaluate(new NudgeContextBuilder().Muting(category: NotificationCategory.Unlock));

        Assert.Equal(new[] { "time-zone" }, Keys(results));
    }

    [Fact]
    public void MutingTheTimeZoneRule_TakesItOffEveryMember()
    {
        var results = Evaluate(new NudgeContextBuilder().TimeZone("UTC").Muting(ruleCode: TimezoneDefaultRule.Code));

        Assert.DoesNotContain("time-zone", Keys(results));
    }

    /// <summary>
    /// "Don't ask me to write it down" is not "don't ask me to re-check it". With notes absent and
    /// the empty rule muted, nothing applies and the step goes; with notes present and stale, the
    /// review question still stands.
    /// </summary>
    [Fact]
    public void MutingOneMedicalRule_LeavesTheOtherInCharge()
    {
        var emptyMuted = Evaluate(new NudgeContextBuilder()
            .NoMedicalNotes()
            .Muting(ruleCode: MedicalNotesEmptyRule.Code));

        var staleButEmptyMuted = Evaluate(new NudgeContextBuilder()
            .NotesReviewedAt(NudgeContextBuilder.Now.AddDays(-200))
            .Muting(ruleCode: MedicalNotesEmptyRule.Code));

        Assert.DoesNotContain("medical-information", Keys(emptyMuted));
        Assert.False(StepOf(staleButEmptyMuted, "medical-information").Done);
    }

    [Fact]
    public void MutingBothMedicalRules_TakesTheStepOff()
    {
        var results = Evaluate(new NudgeContextBuilder()
            .NotesReviewedAt(NudgeContextBuilder.Now.AddDays(-200))
            .Muting(ruleCode: MedicalNotesEmptyRule.Code)
            .Muting(ruleCode: MedicalNotesStaleRule.Code));

        Assert.DoesNotContain("medical-information", Keys(results));
    }

    // ---------------------------------------------------------------- the rules still say the same

    /// <summary>
    /// The refactor the checklist needed moved each rule's predicate into <c>CheckSetup</c>. Wherever
    /// a rule raises a gap, its checklist step must be open — the ring can never read done while a
    /// nudge is asking.
    /// </summary>
    [Fact]
    public void WheneverASetupRuleRaisesAGap_ItsStepIsOpen()
    {
        var contexts = new[]
        {
            new NudgeContextBuilder().NoEmergencyContact().Build(),
            new NudgeContextBuilder().NoMedicalNotes().Build(),
            new NudgeContextBuilder().NotesReviewedAt(NudgeContextBuilder.Now.AddDays(-200)).Build(),
            new NudgeContextBuilder().NotesReviewedAt(null).MemberCreated(NudgeContextBuilder.Now.AddDays(-400)).Build(),
            new NudgeContextBuilder().WithConnections(
                NudgeContextBuilder.Connection(ConnectionStatus.Connected, null, "activity_and_fitness")).Build(),
            new NudgeContextBuilder().WithConnections(NudgeContextBuilder.IrnConnection(enrolled: false)).Build()
        };

        foreach (var context in contexts)
        {
            var results = MemberSetupChecklist.Evaluate(context);

            foreach (var step in MemberSetupChecklist.Steps)
            {
                if (!step.Rules.Any(r => r.Evaluate(context).HasGap))
                    continue;

                Assert.False(StepOf(results, step.Key).Done, $"{step.Key} read done while its nudge was open.");
            }
        }
    }
}
