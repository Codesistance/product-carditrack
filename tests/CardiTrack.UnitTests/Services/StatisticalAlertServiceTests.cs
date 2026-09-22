using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins the pass's orchestration guarantees: no established baseline means total silence
/// (provisional never alerts), cooldowns are scoped to the family's remedy — rule-scoped
/// where remedies differ, type-scoped for the heart — one day's data produces at most one
/// alert per rule regardless of the cadence, and <b>the verdict is the model's</b>: severity,
/// headline and message come from MedGemma's answer, matched to each finding by rule, and any
/// answer that cannot be used raises nothing rather than something code wrote.
/// </summary>
public class StatisticalAlertServiceTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICardiMemberRepository _members = Substitute.For<ICardiMemberRepository>();
    private readonly IPatternBaselineRepository _baselines = Substitute.For<IPatternBaselineRepository>();
    private readonly IUserCardiMemberRepository _links = Substitute.For<IUserCardiMemberRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IActivityLogRepository _activityLogs = Substitute.For<IActivityLogRepository>();
    private readonly IAlertRepository _alerts = Substitute.For<IAlertRepository>();
    private readonly IAlertPreferenceRepository _alertPreferences = Substitute.For<IAlertPreferenceRepository>();
    private readonly IEnvironmentalReadingRepository _environmentalReadings = Substitute.For<IEnvironmentalReadingRepository>();
    private readonly IMedicalAiService _medicalAi = Substitute.For<IMedicalAiService>();
    private readonly IRewriteAiService _rewriteAi = Substitute.For<IRewriteAiService>();
    private readonly IAlertNotificationEnqueue _enqueue = Substitute.For<IAlertNotificationEnqueue>();

    private readonly Guid _memberId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();

    /// <summary>13:40 UTC — 14:40 in London (BST), so local "yesterday" is 2026-08-09.</summary>
    private static readonly DateTime UtcNow = new(2026, 8, 10, 13, 40, 0, DateTimeKind.Utc);
    private static readonly DateOnly Yesterday = new(2026, 8, 9);

    public StatisticalAlertServiceTests()
    {
        _unitOfWork.CardiMembers.Returns(_members);
        _unitOfWork.PatternBaselines.Returns(_baselines);
        _unitOfWork.UserCardiMembers.Returns(_links);
        _unitOfWork.Users.Returns(_users);
        _unitOfWork.ActivityLogs.Returns(_activityLogs);
        _unitOfWork.Alerts.Returns(_alerts);
        _unitOfWork.AlertPreferences.Returns(_alertPreferences);
        _unitOfWork.EnvironmentalReadings.Returns(_environmentalReadings);

        // Defaults: one active London-anchored member with an established baseline, a sharp
        // step decline yesterday, no standing alerts, and a model that answers every rule.
        _members.GetActiveIdsWithActivitySinceAsync(Arg.Any<DateOnly>()).Returns([_memberId]);
        _members.GetByIdAsync(_memberId).Returns(Member());
        _baselines.GetLatestByCardiMemberAsync(_memberId, 30).Returns(EstablishedBaseline());
        _links.GetByCardiMemberIdAsync(_memberId).Returns(
        [
            new UserCardiMember { UserId = _userId, CardiMemberId = _memberId, IsActive = true },
        ]);
        _users.GetByIdAsync(_userId).Returns(new User { Id = _userId, TimeZoneId = "Europe/London" });
        SetupLogs(new ActivityLog { CardiMemberId = _memberId, Date = Yesterday, Steps = 1000 });
        _alerts.GetByCardiMemberAsync(_memberId, activeOnly: false).Returns([]);
        ModelJudges(DefaultVerdicts);
    }

    private CardiMember Member() => new()
    {
        Id = _memberId,
        Name = "Margaret Doe",
        DateOfBirth = new DateOnly(1948, 3, 2),
        Gender = Gender.Female,
        IsActive = true,
    };

    private static PatternBaseline EstablishedBaseline() => new()
    {
        PeriodDays = 30,
        AvgSteps = 6000,
        AvgRestingHeartRate = 62,
        StdDevHeartRate = 2.0m,
        AvgSleepMinutes = 420,
        TypicalWakeTime = new TimeOnly(7, 30),
    };

    private void SetupLogs(params ActivityLog[] logs) =>
        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(logs);

    /// <summary>
    /// A verdict for every rule the pass can ask about, so any fixture reaches an alert. The
    /// severities echo what the rules used to hard-code, which keeps the orchestration tests
    /// readable — the point of the pass is that these now come from the answer, not the rule.
    /// </summary>
    private static readonly IReadOnlyList<TestVerdict> DefaultVerdicts =
    [
        Verdict(StatisticalAlertRules.ActivityDeclineRule, "medium"),
        Verdict(StatisticalAlertRules.IrregularSleepRule, "medium"),
        Verdict(StatisticalAlertRules.ElevatedHeartRateRule, "high"),
        Verdict(StatisticalAlertRules.NoMorningActivityRule, "critical"),
        Verdict(StatisticalAlertRules.LongTermTrendRule, "high"),
        Verdict(StatisticalAlertRules.HeartRateVariabilityDropRule, "high"),
        Verdict(StatisticalAlertRules.OvernightBreathingUpRule, "high"),
        Verdict(StatisticalAlertRules.ElevatedZoneWithoutMovementRule, "high"),
        Verdict(StatisticalAlertRules.DaytimeInactivityBlockRule, "medium"),
    ];

    /// <summary>
    /// One rule's whole journey through both slots, so a test can still say "the model judged this
    /// medium and called it a much quieter day" in a single call. The clinical half supplies the
    /// rule, the severity and the read; the rewrite half supplies the headline and message a
    /// caregiver sees. <see cref="ModelJudges"/> wires both fakes from it, which is what keeps the
    /// split invisible to the twenty-odd tests that only care what ends up on the alert.
    /// </summary>
    private sealed record TestVerdict(
        string Rule, string Severity, string Read, string Headline, string Message);

    private static TestVerdict Verdict(
        string rule,
        string severity,
        string headline = "Quieter than usual",
        string? message = null,
        string read = "Steps well below this person's 30-day usual, with no matching change in resting heart rate.") =>
        new(rule, severity, read, headline,
            message ?? "A quieter day than usual for her. Worth a gentle check-in when you next speak.");

    /// <summary>
    /// Wires the clinical read and the rewrite that writes from it. The rewrite echoes back exactly
    /// the rules it was handed, which is the behaviour the service is entitled to expect rather
    /// than the one it defends against — the dropped-entry case has a test of its own.
    /// </summary>
    private void ModelJudges(IReadOnlyList<TestVerdict> verdicts)
    {
        _medicalAi.GenerateStructuredAsync<StatisticalAlertService.JudgementAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new StatisticalAlertService.JudgementAiResponse
            {
                Verdicts = [.. verdicts.Select(v => new StatisticalAlertService.JudgementVerdict
                {
                    Rule = v.Rule,
                    Severity = v.Severity,
                    Finding = v.Read,
                })],
            });

        _rewriteAi.GenerateStructuredAsync<StatisticalAlertService.JudgementRewriteAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new StatisticalAlertService.JudgementRewriteAiResponse
            {
                Entries = [.. verdicts.Select(v => new StatisticalAlertService.JudgementRewrite
                {
                    Rule = v.Rule,
                    Headline = v.Headline,
                    Message = v.Message,
                })],
            });
    }

    private StatisticalAlertService CreateSut() =>
        new(_unitOfWork, _medicalAi, _rewriteAi, PromptContextFactory.Composer(_unitOfWork),
            InertStatusLineGenerator.Create(), NullLogger<StatisticalAlertService>.Instance, new PassThroughWriteGuard(), _enqueue);

    // ── The verdict is the model's ────────────────────────────────────────────────────────

    [Fact]
    public async Task ASharpDecline_RaisesOneAlert_WithTheModelsSeverityHeadlineAndMessage()
    {
        ModelJudges([Verdict(
            StatisticalAlertRules.ActivityDeclineRule, "medium",
            headline: "A much quieter day",
            message: "She moved far less than usual. Worth asking how she is feeling.")]);

        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(1, raised);
        await _alerts.Received(1).AddAsync(Arg.Is<Alert>(a =>
            a.CardiMemberId == _memberId
            && a.AlertType == AlertType.Inactivity
            && a.Severity == AlertSeverity.Yellow
            && a.Title == "A much quieter day"
            && a.Message == "She moved far less than usual. Worth asking how she is feeling."
            && a.MetricValues!.Contains("\"rule\":\"activity_decline\"")));
        await _unitOfWork.Received(1).SaveChangesAsync();
        await _enqueue.Received(1).EnqueueForAlertAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    /// <summary>The severity is whatever the model said, not what the rule used to carry.</summary>
    [Fact]
    public async Task TheModelsSeverity_IsTheAlerts_WhateverTheRuleUsedToSay()
    {
        ModelJudges([Verdict(StatisticalAlertRules.ActivityDeclineRule, "critical")]);

        await CreateSut().EvaluateAsync(UtcNow);

        await _alerts.Received(1).AddAsync(Arg.Is<Alert>(a => a.Severity == AlertSeverity.Red));
    }

    [Fact]
    public async Task TheModelIsAskedOnce_WithEveryFindingOfThePass()
    {
        // Decline + short sleep + elevated resting HR, all in yesterday's log.
        SetupLogs(new ActivityLog
        {
            CardiMemberId = _memberId,
            Date = Yesterday,
            Steps = 1000,
            SleepMinutes = 200,
            RestingHeartRate = 80,
        });

        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(3, raised);
        await _unitOfWork.Received(1).SaveChangesAsync();
        var prompt = (string)_medicalAi.ReceivedCalls().Single().GetArguments()[0]!;
        Assert.Contains("[FINDINGS]", prompt, StringComparison.Ordinal);
        Assert.Contains("\"rule\": \"activity_decline\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"rule\": \"irregular_sleep\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"rule\": \"elevated_heart_rate\"", prompt, StringComparison.Ordinal);
        Assert.Contains("[PATIENT CONTEXT]", prompt, StringComparison.Ordinal);
    }

    /// <summary>A verdict is matched to its finding by rule, so a reordered answer cannot write
    /// one finding's severity against another.</summary>
    [Fact]
    public async Task AVerdict_IsMatchedByRule_NotByPosition()
    {
        SetupLogs(new ActivityLog { CardiMemberId = _memberId, Date = Yesterday, Steps = 1000, RestingHeartRate = 80 });
        ModelJudges([
            Verdict(StatisticalAlertRules.ElevatedHeartRateRule, "critical"),
            Verdict(StatisticalAlertRules.ActivityDeclineRule, "medium"),
        ]);

        await CreateSut().EvaluateAsync(UtcNow);

        await _alerts.Received(1).AddAsync(Arg.Is<Alert>(a =>
            a.AlertType == AlertType.HeartRate && a.Severity == AlertSeverity.Red));
        await _alerts.Received(1).AddAsync(Arg.Is<Alert>(a =>
            a.AlertType == AlertType.Inactivity && a.Severity == AlertSeverity.Yellow));
    }

    // ── Fail closed ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ASeverityOutsideTheTaxonomy_RaisesNothing()
    {
        ModelJudges([Verdict(StatisticalAlertRules.ActivityDeclineRule, "urgent-ish")]);

        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(0, raised);
        await _alerts.DidNotReceive().AddAsync(Arg.Any<Alert>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    [Fact]
    public async Task AVerdictTheModelDidNotGive_RaisesNothing()
    {
        ModelJudges([Verdict(StatisticalAlertRules.ElevatedHeartRateRule, "high")]);

        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(0, raised);
        await _alerts.DidNotReceive().AddAsync(Arg.Any<Alert>());
    }

    /// <summary>Low is the model saying "not worth attention today". Nothing is written, and
    /// nothing marks the day as judged, so the next pass asks again with the day's new readings.</summary>
    [Fact]
    public async Task ALowVerdict_RaisesNothing()
    {
        ModelJudges([Verdict(StatisticalAlertRules.ActivityDeclineRule, "low")]);

        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(0, raised);
        await _alerts.DidNotReceive().AddAsync(Arg.Any<Alert>());
    }

    [Fact]
    public async Task AModelFailure_RaisesNothing_AndLeavesTheFindingForTheNextPass()
    {
        _medicalAi.GenerateStructuredAsync<StatisticalAlertService.JudgementAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("MedGemma cold"));

        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(0, raised);
        await _alerts.DidNotReceive().AddAsync(Arg.Any<Alert>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    /// <summary>
    /// The rewrite runs on the other slot and can fail on its own — a Vertex timeout, a 429, a
    /// region that stopped serving the model. It fails closed like every other exit here: nothing
    /// is persisted, so the next pass re-judges the finding. Writing the alert from copy of our own
    /// would put the hard-coded sentence back in a loop this service exists to keep it out of.
    /// </summary>
    [Fact]
    public async Task ARewriteFailure_RaisesNothing_AndLeavesTheFindingForTheNextPass()
    {
        ModelJudges([Verdict(StatisticalAlertRules.ActivityDeclineRule, "medium")]);
        _rewriteAi.GenerateStructuredAsync<StatisticalAlertService.JudgementRewriteAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("Vertex unavailable"));

        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(0, raised);
        await _alerts.DidNotReceive().AddAsync(Arg.Any<Alert>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    /// <summary>
    /// The same failure that cost activity_decline every verdict it was given on 2026-09-22, one
    /// stage later: entries are matched to their read by exact rule, so a rewrite that drops or
    /// renames one must not have another read's copy written against it. Both stages now constrain
    /// the rule to the eleven, but the match stays defensive.
    /// </summary>
    [Fact]
    public async Task TheRewriteDropsAnEntry_RaisesNothingForThatRule()
    {
        ModelJudges([Verdict(StatisticalAlertRules.ActivityDeclineRule, "medium")]);
        _rewriteAi.GenerateStructuredAsync<StatisticalAlertService.JudgementRewriteAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new StatisticalAlertService.JudgementRewriteAiResponse { Entries = [] });

        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(0, raised);
        await _alerts.DidNotReceive().AddAsync(Arg.Any<Alert>());
    }

    /// <summary>
    /// A severity with no read behind it leaves the rewrite nothing to write from. The second call
    /// is not made at all — a finding that cannot be written about must not cost the pass a Vertex
    /// call to discover it.
    /// </summary>
    [Fact]
    public async Task ABlankClinicalRead_RaisesNothing_AndNeverCallsTheRewrite()
    {
        _medicalAi.GenerateStructuredAsync<StatisticalAlertService.JudgementAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new StatisticalAlertService.JudgementAiResponse
            {
                Verdicts =
                [
                    new StatisticalAlertService.JudgementVerdict
                    {
                        Rule = StatisticalAlertRules.ActivityDeclineRule,
                        Severity = "medium",
                        Finding = "   ",
                    },
                ],
            });

        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(0, raised);
        await _alerts.DidNotReceive().AddAsync(Arg.Any<Alert>());
        await _rewriteAi.DidNotReceive().GenerateStructuredAsync<StatisticalAlertService.JudgementRewriteAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Every finding a member has judged worth raising is rewritten in one call, matching the
    /// clinical half's own batching. Splitting it per finding would have doubled the pass's
    /// inference bill to say the same thing in more calls.
    /// </summary>
    [Fact]
    public async Task SeveralFindings_AreRewrittenInOneCall()
    {
        SetupLogs(
            new ActivityLog { CardiMemberId = _memberId, Date = Yesterday, Steps = 1000, RestingHeartRate = 80 });
        ModelJudges(DefaultVerdicts);

        await CreateSut().EvaluateAsync(UtcNow);

        await _rewriteAi.Received(1).GenerateStructuredAsync<StatisticalAlertService.JudgementRewriteAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// DPIA row A20's boundary, asserted rather than assumed: the Rewrite slot is given the
    /// clinical reads and nothing else. DemographicsContextSource decrypts caregiver notes without
    /// redacting the member's name, and MedGemma can repeat that name in its read, so the
    /// redaction runs on the way across.
    /// </summary>
    [Fact]
    public async Task TheRewritePrompt_CarriesNoMemberIdentity()
    {
        ModelJudges([Verdict(
            StatisticalAlertRules.ActivityDeclineRule, "medium",
            read: "Margaret Doe's steps sat well below her 30-day usual.")]);

        await CreateSut().EvaluateAsync(UtcNow);

        var prompt = (string)_rewriteAi.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IRewriteAiService.GenerateStructuredAsync))
            .GetArguments()[0]!;

        Assert.DoesNotContain("Margaret", prompt, StringComparison.OrdinalIgnoreCase);
        // Word-bounded: the surname is a substring of "does", which the tone block uses.
        Assert.DoesNotMatch(@"\bDoe\b", prompt);
        Assert.DoesNotContain("1948", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyMessage_RaisesNothing()
    {
        ModelJudges([Verdict(StatisticalAlertRules.ActivityDeclineRule, "medium", message: "   ")]);

        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(0, raised);
    }

    /// <summary>A pronoun the record does not bear out is a claim about someone's mother or
    /// father; the alert is withheld, on the same terms as every other rewrite guard.</summary>
    [Fact]
    public async Task AMessageStatingAnUnsupportedSex_RaisesNothing()
    {
        ModelJudges([Verdict(
            StatisticalAlertRules.ActivityDeclineRule, "medium",
            message: "He moved far less than usual. Worth asking how he is feeling.")]);

        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(0, raised);
    }

    /// <summary>Severity still routes when the sentence names a condition; the sentence does not.</summary>
    [Fact]
    public async Task AMessageNamingACondition_KeepsTheSeverity_AndLosesTheSentence()
    {
        ModelJudges([Verdict(
            StatisticalAlertRules.ActivityDeclineRule, "high",
            message: "This pattern is consistent with atrial fibrillation. Call her doctor.")]);

        await CreateSut().EvaluateAsync(UtcNow);

        await _alerts.Received(1).AddAsync(Arg.Is<Alert>(a =>
            a.Severity == AlertSeverity.Orange
            && a.Message == StatisticalAlertService.NonClinicalObservation));
    }

    /// <summary>A headline that is not a title — empty, a sentence, a leftover token — falls back
    /// to the settings catalogue's own name for the rule: the observation the caregiver chose to
    /// be told about, not a verdict.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("Everything about the day looked much quieter than it usually does for her")]
    [InlineData("CardiTrackCardiMemberTheir quieter day")]
    public async Task AnUnusableHeadline_FallsBackToTheCatalogueName(string headline)
    {
        ModelJudges([Verdict(StatisticalAlertRules.ActivityDeclineRule, "medium", headline: headline)]);

        await CreateSut().EvaluateAsync(UtcNow);

        await _alerts.Received(1).AddAsync(Arg.Is<Alert>(a => a.Title == "Activity decline"));
    }

    // ── Provisional never alerts, preferences, cooldown, dedup — all ahead of the model ──

    // Provisional-never-alerts, for the COMPARATIVE rules: no 30-day baseline, nothing that asks
    // "is this unusual for them" runs, and no inference is spent. The read is narrowed rather than
    // skipped outright — the measured-rule test below is what it is narrowed *for*.
    [Fact]
    public async Task NoEstablishedBaseline_SilencesTheComparativeRules()
    {
        _baselines.GetLatestByCardiMemberAsync(_memberId, 30).Returns((PatternBaseline?)null);

        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(0, raised);
        Assert.Empty(_medicalAi.ReceivedCalls());

        // Two days, not four weeks: without a baseline the trend rule cannot run, so nothing
        // reads further back than the measured rules need.
        await _activityLogs.Received(1).GetByCardiMemberAndDateRangeAsync(
            _memberId,
            Arg.Is<DateOnly>(from => from == DateOnly.FromDateTime(UtcNow).AddDays(-1)),
            Arg.Any<DateOnly>());
    }

    /// <summary>
    /// The silence this engine must never produce: a member two weeks into wearing a watch has no
    /// established baseline and exactly the same heart. A finding the device itself made carries
    /// no inference for a thin window to weaken, so it is judged like any other.
    /// </summary>
    [Fact]
    public async Task NoEstablishedBaseline_StillReportsAFindingTheDeviceItselfMade()
    {
        _baselines.GetLatestByCardiMemberAsync(_memberId, 30).Returns((PatternBaseline?)null);
        _activityLogs.GetByCardiMemberAndDateRangeAsync(
                _memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(new List<ActivityLog>
            {
                new()
                {
                    CardiMemberId = _memberId,
                    Date = DateOnly.FromDateTime(UtcNow),
                    EcgReadings = 1,
                    EcgAtrialFibrillationReadings = 1,
                },
            });

        ModelJudges([Verdict(StatisticalAlertRules.EcgAtrialFibrillationRule, "critical")]);

        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(1, raised);
        await _alerts.Received(1).AddAsync(Arg.Is<Alert>(a => a.AlertType == AlertType.Rhythm));
    }

    [Fact]
    public async Task DisabledRule_IsNotEvaluatedAtAll()
    {
        _alertPreferences.GetByCardiMemberIdAsync(_memberId).Returns(new AlertPreference
        {
            CardiMemberId = _memberId,
            DisabledRules = """["activity_decline"]""",
        });

        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(0, raised);
        await _alerts.DidNotReceive().AddAsync(Arg.Any<Alert>());
        Assert.Empty(_medicalAi.ReceivedCalls());
    }

    /// <summary>Cooldown and dedup run before the model, so a finding that would be suppressed
    /// costs no inference.</summary>
    [Fact]
    public async Task AnUnresolvedAlertOfTheSameRule_Suppresses_BeforeTheModelIsAsked()
    {
        _alerts.GetByCardiMemberAsync(_memberId, activeOnly: false).Returns(
        [
            new Alert
            {
                CardiMemberId = _memberId, AlertType = AlertType.Inactivity, IsResolved = false,
                MetricValues = """{"rule":"activity_decline","steps":900}""",
            },
        ]);

        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(0, raised);
        Assert.Empty(_medicalAi.ReceivedCalls());
    }

    // Device-silence and activity-decline share the Inactivity type but ask for different
    // remedies — charging the watch does not resolve a quiet day, so neither suppresses the other.
    [Fact]
    public async Task AnUnresolvedDeviceSilenceAlert_DoesNotSuppressActivityDecline()
    {
        _alerts.GetByCardiMemberAsync(_memberId, activeOnly: false).Returns(
        [
            new Alert
            {
                CardiMemberId = _memberId, AlertType = AlertType.Inactivity, IsResolved = false,
                MetricValues = """{"rule":"device_silence","thresholdMinutes":120}""",
            },
        ]);

        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(1, raised);
    }

    // A pre-marker Inactivity alert could only have come from the device-silence producer, so
    // it reads as device-silence: it must not suppress the decline rule either.
    [Fact]
    public async Task ALegacyMarkerlessInactivityAlert_ReadsAsDeviceSilence()
    {
        _alerts.GetByCardiMemberAsync(_memberId, activeOnly: false).Returns(
        [
            new Alert
            {
                CardiMemberId = _memberId, AlertType = AlertType.Inactivity, IsResolved = false,
                MetricValues = """{"lastDataUtc":null,"thresholdMinutes":120}""",
            },
        ]);

        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(1, raised);
    }

    // The heart is type-scoped across producers: an unresolved AI assessment alert suppresses
    // the statistical rule too — same organ, same remedy, one page.
    [Fact]
    public async Task AnUnresolvedAssessorHeartAlert_SuppressesTheStatisticalHeartRule()
    {
        SetupLogs(new ActivityLog
        {
            CardiMemberId = _memberId,
            Date = Yesterday,
            Steps = 6000,
            RestingHeartRate = 80,
        });
        _alerts.GetByCardiMemberAsync(_memberId, activeOnly: false).Returns(
        [
            new Alert
            {
                CardiMemberId = _memberId, AlertType = AlertType.HeartRate, IsResolved = false,
                MetricValues = """{"rule":"realtime_hr","hrTrendLast":95.0}""",
            },
        ]);

        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(0, raised);
    }

    // One day's data gets at most one alert per rule: resolving at noon must not re-page at
    // half past from the same readings.
    [Fact]
    public async Task AResolvedAlertFromToday_StillDedupes()
    {
        _alerts.GetByCardiMemberAsync(_memberId, activeOnly: false).Returns(
        [
            new Alert
            {
                CardiMemberId = _memberId, AlertType = AlertType.Inactivity, IsResolved = true,
                TriggeredDate = UtcNow.AddHours(-2),
                MetricValues = """{"rule":"activity_decline","steps":1000}""",
            },
        ]);

        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(0, raised);
    }

    // Deleting is housekeeping, not a new episode: the same quieter day must not page again
    // five minutes later just because the card left the list.
    [Fact]
    public async Task ADeletedAlertFromToday_StillDedupes()
    {
        _alerts.GetByCardiMemberAsync(_memberId, activeOnly: false).Returns(
        [
            new Alert
            {
                CardiMemberId = _memberId, AlertType = AlertType.Inactivity, IsResolved = false,
                IsActive = false,
                TriggeredDate = UtcNow.AddHours(-2),
                MetricValues = """{"rule":"activity_decline","steps":1000}""",
            },
        ]);

        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(0, raised);
        await _alerts.DidNotReceive().AddAsync(Arg.Any<Alert>());
    }

    // A deleted statistical alert must not latch the rule forever — this pass does not
    // auto-resolve, so yesterday's deleted quieter-day card is spent history, not a cooldown.
    [Fact]
    public async Task ADeletedAlertFromYesterday_DoesNotDedupeToday()
    {
        _alerts.GetByCardiMemberAsync(_memberId, activeOnly: false).Returns(
        [
            new Alert
            {
                CardiMemberId = _memberId, AlertType = AlertType.Inactivity, IsResolved = false,
                IsActive = false,
                TriggeredDate = UtcNow.AddDays(-1),
                MetricValues = """{"rule":"activity_decline","steps":1000}""",
            },
        ]);

        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task ADeletedSleepAlertForTheSameNight_StillDedupes()
    {
        SetupLogs(
            new ActivityLog { CardiMemberId = _memberId, Date = Yesterday, Steps = 6000, SleepMinutes = 200 },
            new ActivityLog { CardiMemberId = _memberId, Date = Yesterday.AddDays(1), Steps = 4500 });
        _alerts.GetByCardiMemberAsync(_memberId, activeOnly: false).Returns(
        [
            new Alert
            {
                CardiMemberId = _memberId, AlertType = AlertType.Sleep, IsResolved = false,
                IsActive = false,
                TriggeredDate = UtcNow.AddDays(-1),
                MetricValues = """{"rule":"irregular_sleep","night":"2026-08-09","sleepMinutes":200}""",
            },
        ]);

        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task AResolvedAlertFromYesterday_DoesNotDedupeToday()
    {
        _alerts.GetByCardiMemberAsync(_memberId, activeOnly: false).Returns(
        [
            new Alert
            {
                CardiMemberId = _memberId, AlertType = AlertType.Inactivity, IsResolved = true,
                TriggeredDate = UtcNow.AddDays(-1),
                MetricValues = """{"rule":"activity_decline","steps":1000}""",
            },
        ]);

        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(1, raised);
    }

    // ── The sleep rule judges last night, which lives on TODAY's log ─────────────────────
    //
    // Sleep sessions are attributed to the civil day they ended on, so the night a family is
    // looking at this morning is today's row — the same row the dashboard's sleep card rates.

    [Fact]
    public async Task AShortNightOnTodaysLog_AlertsTheSameDay()
    {
        SetupLogs(
            new ActivityLog { CardiMemberId = _memberId, Date = Yesterday, Steps = 6000 },
            new ActivityLog { CardiMemberId = _memberId, Date = Yesterday.AddDays(1), SleepMinutes = 216 });

        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(1, raised);
        await _alerts.Received(1).AddAsync(Arg.Is<Alert>(a =>
            a.AlertType == AlertType.Sleep
            && a.MetricValues!.Contains("\"rule\":\"irregular_sleep\"")
            && a.MetricValues!.Contains("\"night\":\"2026-08-10\"")));
    }

    // Once today's row carries a sleep reading, yesterday's row is the night before last —
    // old news, already judged on its own day, never a reason to page today.
    [Fact]
    public async Task AnOrdinaryNightOnTodaysLog_IsNeverOverriddenByYesterdays()
    {
        SetupLogs(
            new ActivityLog { CardiMemberId = _memberId, Date = Yesterday, Steps = 6000, SleepMinutes = 200 },
            new ActivityLog { CardiMemberId = _memberId, Date = Yesterday.AddDays(1), SleepMinutes = 420 });

        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(0, raised);
    }

    // The fallback: a night whose data only arrived after local midnight sits on yesterday's
    // log with nothing on today's yet — better judged late than never.
    [Fact]
    public async Task ANightThatSyncedLate_IsStillJudged_FromYesterdaysLog()
    {
        SetupLogs(
            new ActivityLog { CardiMemberId = _memberId, Date = Yesterday, Steps = 6000, SleepMinutes = 200 },
            new ActivityLog { CardiMemberId = _memberId, Date = Yesterday.AddDays(1), Steps = 4500 });

        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(1, raised);
        await _alerts.Received(1).AddAsync(Arg.Is<Alert>(a =>
            a.AlertType == AlertType.Sleep && a.MetricValues!.Contains("\"night\":\"2026-08-09\"")));
    }

    // What keeps the fallback honest: the dedup is per night judged, not per firing day, so a
    // night that already alerted — even on a different calendar day — never alerts again.
    [Fact]
    public async Task AResolvedAlertForTheSameNight_StillDedupes_AcrossCalendarDays()
    {
        SetupLogs(
            new ActivityLog { CardiMemberId = _memberId, Date = Yesterday, Steps = 6000, SleepMinutes = 200 },
            new ActivityLog { CardiMemberId = _memberId, Date = Yesterday.AddDays(1), Steps = 4500 });
        _alerts.GetByCardiMemberAsync(_memberId, activeOnly: false).Returns(
        [
            new Alert
            {
                CardiMemberId = _memberId, AlertType = AlertType.Sleep, IsResolved = true,
                TriggeredDate = UtcNow.AddDays(-1),
                MetricValues = """{"rule":"irregular_sleep","night":"2026-08-09","sleepMinutes":200}""",
            },
        ]);

        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task AResolvedSleepAlertForAnEarlierNight_DoesNotDedupeTonights()
    {
        SetupLogs(
            new ActivityLog { CardiMemberId = _memberId, Date = Yesterday, Steps = 6000 },
            new ActivityLog { CardiMemberId = _memberId, Date = Yesterday.AddDays(1), SleepMinutes = 216 });
        _alerts.GetByCardiMemberAsync(_memberId, activeOnly: false).Returns(
        [
            new Alert
            {
                CardiMemberId = _memberId, AlertType = AlertType.Sleep, IsResolved = true,
                TriggeredDate = UtcNow.AddDays(-1),
                MetricValues = """{"rule":"irregular_sleep","night":"2026-08-09","sleepMinutes":180}""",
            },
        ]);

        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(1, raised);
    }

    // A sleep alert from before night markers existed cannot say which night it judged, so it
    // reads as the day it fired on — one page per day still holds across the deploy boundary.
    [Fact]
    public async Task ALegacyMarkerlessSleepAlertFromToday_StillDedupes()
    {
        SetupLogs(
            new ActivityLog { CardiMemberId = _memberId, Date = Yesterday, Steps = 6000 },
            new ActivityLog { CardiMemberId = _memberId, Date = Yesterday.AddDays(1), SleepMinutes = 216 });
        _alerts.GetByCardiMemberAsync(_memberId, activeOnly: false).Returns(
        [
            new Alert
            {
                CardiMemberId = _memberId, AlertType = AlertType.Sleep, IsResolved = true,
                TriggeredDate = UtcNow.AddHours(-2),
                MetricValues = """{"rule":"irregular_sleep","sleepMinutes":216}""",
            },
        ]);

        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task NoMorningActivity_Fires_OnAMeasuredZeroToday()
    {
        SetupLogs(
            new ActivityLog { CardiMemberId = _memberId, Date = Yesterday, Steps = 6000 },
            new ActivityLog { CardiMemberId = _memberId, Date = Yesterday.AddDays(1), Steps = 0 });

        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(1, raised);
        await _alerts.Received(1).AddAsync(Arg.Is<Alert>(a =>
            a.AlertType == AlertType.PatternBreak && a.Severity == AlertSeverity.Red));
    }

    // Wiring, not thresholds: the pure rules are pinned in StatisticalAlertRulesTests, but only
    // EvaluateAsync can catch a rule registered under the wrong preference id, reading the wrong
    // row, or losing its finding to the shared HeartRate cooldown.
    [Fact]
    public async Task TheOvernightBreathingRule_RaisesThroughTheOrchestrator()
    {
        var baseline = EstablishedBaseline();
        baseline.AvgOvernightBreathingRate = 14m;
        baseline.StdDevOvernightBreathingRate = 0.4m;
        _baselines.GetLatestByCardiMemberAsync(_memberId, 30).Returns(baseline);

        SetupLogs(
            new ActivityLog { CardiMemberId = _memberId, Date = Yesterday, Steps = 6000 },
            new ActivityLog
            {
                CardiMemberId = _memberId,
                Date = Yesterday.AddDays(1),
                OvernightBreathingRate = 17m,
            });

        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(1, raised);
        await _alerts.Received(1).AddAsync(Arg.Is<Alert>(a =>
            a.MetricValues!.Contains("\"rule\":\"overnight_breathing_up\"")
            && a.MetricValues!.Contains("\"night\":\"2026-08-10\"")));
    }

    [Fact]
    public async Task TheElevatedZoneRule_RaisesThroughTheOrchestrator_OnYesterdaysRow()
    {
        SetupLogs(new ActivityLog
        {
            CardiMemberId = _memberId,
            Date = Yesterday,
            Steps = 1000,
            ModerateZoneMinutes = 40,
        });

        var raised = await CreateSut().EvaluateAsync(UtcNow);

        // The step decline fires too — they are the same quiet day read two ways, which is the
        // point of the pairing, and the model reads them together in one call.
        Assert.Equal(2, raised);
        Assert.Single(_medicalAi.ReceivedCalls());
        await _alerts.Received(1).AddAsync(Arg.Is<Alert>(a =>
            a.MetricValues!.Contains("\"rule\":\"elevated_zone_without_movement\"")));
    }

    [Fact]
    public async Task TheInactivityBlockRule_RaisesThroughTheOrchestrator_OnYesterdaysRow()
    {
        SetupLogs(new ActivityLog
        {
            CardiMemberId = _memberId,
            Date = Yesterday,
            Steps = 6000,
            LongestSedentaryStretchMinutes = 300,
        });

        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(1, raised);
        await _alerts.Received(1).AddAsync(Arg.Is<Alert>(a =>
            a.AlertType == AlertType.Inactivity
            && a.MetricValues!.Contains("\"rule\":\"daytime_inactivity_block\"")));
    }

    // Overnight vitals share sleep's attribution but not its payload: a device can post the
    // night's HRV while the sleep session is still syncing. Gating them on SleepMinutes sent both
    // overnight rules a day back, to a night they had already judged.
    [Fact]
    public async Task OvernightVitalsAreJudgedOnTodaysRow_EvenWithNoSleepSessionOnIt()
    {
        var baseline = EstablishedBaseline();
        baseline.AvgHeartRateVariabilityMs = 40m;
        baseline.StdDevHeartRateVariability = 2m;
        _baselines.GetLatestByCardiMemberAsync(_memberId, 30).Returns(baseline);

        // Steps at their usual, so the HRV rule is the only one in play.
        SetupLogs(
            new ActivityLog
            {
                CardiMemberId = _memberId,
                Date = Yesterday,
                Steps = 6000,
                HeartRateVariabilityMs = 26m,
            },
            new ActivityLog
            {
                CardiMemberId = _memberId,
                Date = Yesterday.AddDays(1),
                HeartRateVariabilityMs = 25m,
            });

        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(1, raised);
        await _alerts.Received(1).AddAsync(Arg.Is<Alert>(a =>
            a.MetricValues!.Contains("\"rule\":\"hrv_drop\"")
            && a.MetricValues!.Contains("\"night\":\"2026-08-10\"")));
    }

    [Fact]
    public async Task APausedMember_IsNotEvaluated()
    {
        var paused = Member();
        paused.MonitoringPausedUntil = UtcNow.AddDays(1);
        _members.GetByIdAsync(_memberId).Returns(paused);

        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(0, raised);
        await _baselines.DidNotReceive().GetLatestByCardiMemberAsync(Arg.Any<Guid>(), Arg.Any<int>());
    }

    [Fact]
    public async Task OneMemberFailure_DoesNotCostTheRestThePass()
    {
        var otherId = Guid.NewGuid();
        _members.GetActiveIdsWithActivitySinceAsync(Arg.Any<DateOnly>()).Returns([otherId, _memberId]);
        _members.GetByIdAsync(otherId).Throws(new InvalidOperationException("db hiccup"));

        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(1, raised);
    }
}
