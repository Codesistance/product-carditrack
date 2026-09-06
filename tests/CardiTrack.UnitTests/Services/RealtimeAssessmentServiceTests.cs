using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins the assessment pass's guarantees: the model is only consulted when a full fresh window
/// of heart rate exists, has not been assessed before, and SSA says the latest reading jumped
/// from trend; a red or orange verdict raises exactly one alert per episode and asks the API
/// to push it; and an answer the parser cannot read is stored but routes nowhere.
/// </summary>
public class RealtimeAssessmentServiceTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICardiMemberRepository _members = Substitute.For<ICardiMemberRepository>();
    private readonly IGranularMetricRepository _granular = Substitute.For<IGranularMetricRepository>();
    private readonly IRealtimeAssessmentRepository _assessments = Substitute.For<IRealtimeAssessmentRepository>();
    private readonly IEnvironmentalReadingRepository _environmentalReadings = Substitute.For<IEnvironmentalReadingRepository>();
    private readonly IAlertRepository _alerts = Substitute.For<IAlertRepository>();
    private readonly IAlertPreferenceRepository _alertPreferences = Substitute.For<IAlertPreferenceRepository>();
    private readonly IMedicalAiService _medicalAi = Substitute.For<IMedicalAiService>();
    private readonly IAlertNotificationEnqueue _enqueue = Substitute.For<IAlertNotificationEnqueue>();

    private readonly Guid _memberId = Guid.NewGuid();

    /// <summary>Mid-hour, so the fetched range is 11:00–15:00 and the guards see a part-filled
    /// final hour — the shape every real pass sees.</summary>
    private static readonly DateTime UtcNow = new(2026, 8, 10, 14, 30, 0, DateTimeKind.Utc);

    /// <summary>Start of the fetched range for <see cref="UtcNow"/>: four whole hours back from
    /// the end of the in-progress hour.</summary>
    private static readonly DateTime RangeStart = new(2026, 8, 10, 11, 0, 0, DateTimeKind.Utc);

    private const int RangeMinutes = 240;

    public RealtimeAssessmentServiceTests()
    {
        _unitOfWork.CardiMembers.Returns(_members);
        _unitOfWork.GranularMetrics.Returns(_granular);
        _unitOfWork.RealtimeAssessments.Returns(_assessments);
        _unitOfWork.EnvironmentalReadings.Returns(_environmentalReadings);
        _unitOfWork.Alerts.Returns(_alerts);
        _unitOfWork.AlertPreferences.Returns(_alertPreferences);

        // Defaults: one active member with a full, never-assessed heart-rate hour ending 14:30,
        // no unresolved alerts, and a model that answers "low".
        _members.GetActiveIdsWithActivitySinceAsync(Arg.Any<DateOnly>()).Returns([_memberId]);
        _members.GetByIdAsync(_memberId).Returns(Member());
        SetupWindow(HeartRateMinutes(from: 150, to: 209, bpm: 72));
        _assessments.ExistsAsync(_memberId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _assessments.UpsertAsync(Arg.Any<RealtimeAssessment>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _alerts.GetByCardiMemberAsync(_memberId, activeOnly: false).Returns([]);
        _medicalAi.GenerateStructuredAsync<RealtimeAssessmentService.AssessmentAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RealtimeAssessmentService.AssessmentAiResponse
            {
                Message = "A steady hour, nothing unusual.",
                Severity = "low",
            });
    }

    private CardiMember Member() => new()
    {
        Id = _memberId,
        Name = "Margaret Doe",
        DateOfBirth = new DateOnly(1948, 3, 2),
        Gender = Gender.Female,
        IsActive = true,
        MedicalNotes = "On beta blockers.",
    };

    /// <summary>A range-length heart-rate series with values on minutes [from, to]. The last
    /// sample jumps well past SSA's ordinary-variation gate so the default fixture reaches the
    /// model; pass <paramref name="jumpLast"/> false for a calm hour that must skip inference.</summary>
    private static float?[] HeartRateMinutes(int from, int to, float bpm, bool jumpLast = true)
    {
        var series = new float?[RangeMinutes];
        for (var i = from; i <= to; i++)
            series[i] = bpm + (i % 3);  // a little jitter so SSA has a noise floor to find
        if (jumpLast && to >= from)
            series[to] = bpm + 25;
        return series;
    }

    private void SetupWindow(float?[] heartRate, float?[]? steps = null, float?[]? spo2 = null)
    {
        var series = new Dictionary<GranularMetric, float?[]> { [GranularMetric.HeartRate] = heartRate };
        if (steps is not null)
            series[GranularMetric.Steps] = steps;
        if (spo2 is not null)
            series[GranularMetric.SpO2] = spo2;
        SetupWindowSeries(series);
    }

    private void SetupWindowSeries(Dictionary<GranularMetric, float?[]> series)
    {
        _granular.GetWindowAsync(_memberId, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new GranularWindow
            {
                CardiMemberId = _memberId,
                FromUtc = RangeStart,
                ToUtc = RangeStart.AddMinutes(RangeMinutes),
                MinuteSeries = series,
            });
    }

    private RealtimeAssessmentService CreateSut() =>
        new(_unitOfWork, new SsaDecomposition(), _medicalAi, PromptContextFactory.Composer(_unitOfWork),
            InertStatusLineGenerator.Create(), NullLogger<RealtimeAssessmentService>.Instance, _enqueue);

    [Fact]
    public async Task AFullFreshHour_IsAssessed_AndStoredUnderItsWindowStart()
    {
        var assessed = await CreateSut().AssessDueMembersAsync(UtcNow);

        Assert.Equal(1, assessed);
        await _assessments.Received(1).UpsertAsync(
            Arg.Is<RealtimeAssessment>(a =>
                a.CardiMemberId == _memberId
                // Data ends at minute 209 → window is 13:30–14:30.
                && a.WindowStartUtc == RangeStart.AddMinutes(150)
                && a.WindowEndUtc == RangeStart.AddMinutes(210)
                && a.Severity == AlertSeverity.Green
                && a.RawSeverity == "low"
                && a.SsaEngine == SsaParameters.Engine),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ANamedCondition_IsReplaced_WhileSeverityStillRoutes()
    {
        _medicalAi.GenerateStructuredAsync<RealtimeAssessmentService.AssessmentAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RealtimeAssessmentService.AssessmentAiResponse
            {
                Message = "This hour is consistent with tachycardia and may indicate arrhythmia.",
                Severity = "high",
            });

        await CreateSut().AssessDueMembersAsync(UtcNow);

        await _assessments.Received(1).UpsertAsync(
            Arg.Is<RealtimeAssessment>(a =>
                a.ModelOutput == RealtimeAssessmentService.NonClinicalObservation
                && a.Severity == AlertSeverity.Orange
                && a.RawSeverity == "high"),
            Arg.Any<CancellationToken>());
        await _alerts.Received(1).AddAsync(Arg.Is<Alert>(a =>
            a.Message == RealtimeAssessmentService.NonClinicalObservation
            && a.Severity == AlertSeverity.Orange));
    }

    [Fact]
    public async Task AGreenVerdict_RaisesNoAlert()
    {
        await CreateSut().AssessDueMembersAsync(UtcNow);

        await _alerts.DidNotReceive().AddAsync(Arg.Any<Alert>());
        await _enqueue.DidNotReceive().EnqueueForAlertAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // SSA is the cost control that makes a 5-minute cadence affordable: a calm hour is scored
    // every pass (cheap) and never sent to MedGemma. The row is not stored, so a later tick
    // can still consult the model if the same hour later jumps.
    [Fact]
    public async Task AnOrdinaryWindow_CostsNoInference_AndIsNotStored()
    {
        SetupWindow(HeartRateMinutes(from: 150, to: 209, bpm: 72, jumpLast: false));

        var assessed = await CreateSut().AssessDueMembersAsync(UtcNow);

        Assert.Equal(0, assessed);
        await _medicalAi.DidNotReceive().GenerateStructuredAsync<RealtimeAssessmentService.AssessmentAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _assessments.DidNotReceive().UpsertAsync(Arg.Any<RealtimeAssessment>(), Arg.Any<CancellationToken>());
        await _enqueue.DidNotReceive().EnqueueForAlertAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // The SSA skip used to return before the only automatic resolve. An episode that had
    // ended would stay open, and RaiseAlertAsync's cooldown would never re-arm.
    [Fact]
    public async Task AnOrdinaryWindow_ResolvesAnOpenHeartRateAlert_WithoutInference()
    {
        SetupWindow(HeartRateMinutes(from: 150, to: 209, bpm: 72, jumpLast: false));
        var open = new Alert { CardiMemberId = _memberId, AlertType = AlertType.HeartRate, IsResolved = false };
        _alerts.GetByCardiMemberAsync(_memberId, activeOnly: false).Returns([open]);

        var assessed = await CreateSut().AssessDueMembersAsync(UtcNow);

        Assert.Equal(0, assessed);
        Assert.True(open.IsResolved);
        await _unitOfWork.Received(1).SaveChangesAsync();
        await _medicalAi.DidNotReceive().GenerateStructuredAsync<RealtimeAssessmentService.AssessmentAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _assessments.DidNotReceive().UpsertAsync(Arg.Any<RealtimeAssessment>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFailedEnqueue_DoesNotLoseTheAlert()
    {
        _medicalAi.GenerateStructuredAsync<RealtimeAssessmentService.AssessmentAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RealtimeAssessmentService.AssessmentAiResponse
            {
                Message = "Heart rate has risen sharply with no activity.",
                Severity = "critical",
            });
        _enqueue.EnqueueForAlertAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("API 503"));

        var assessed = await CreateSut().AssessDueMembersAsync(UtcNow);

        Assert.Equal(1, assessed);
        await _alerts.Received(1).AddAsync(Arg.Any<Alert>());
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    [Theory]
    [InlineData("critical", AlertSeverity.Red)]
    [InlineData("high", AlertSeverity.Orange)]
    public async Task ARedOrOrangeVerdict_RaisesAHeartRateAlert(string word, AlertSeverity expected)
    {
        _medicalAi.GenerateStructuredAsync<RealtimeAssessmentService.AssessmentAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RealtimeAssessmentService.AssessmentAiResponse
            {
                Message = "Heart rate has risen sharply with no activity.",
                Severity = word,
            });

        await CreateSut().AssessDueMembersAsync(UtcNow);

        await _alerts.Received(1).AddAsync(Arg.Is<Alert>(a =>
            a.CardiMemberId == _memberId
            && a.AlertType == AlertType.HeartRate
            && a.Severity == expected
            && a.Message.Contains("risen sharply")
            && a.MetricValues!.Contains("hrDeviationScore")));
        await _unitOfWork.Received(1).SaveChangesAsync();
        await _enqueue.Received(1).EnqueueForAlertAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // Twelve pages an hour about one sustained event teaches families to ignore the pager —
    // resolving the standing alert is what re-arms the path.
    [Fact]
    public async Task AnUnresolvedHeartRateAlert_SuppressesANewOne_ButTheAssessmentIsStillWritten()
    {
        _medicalAi.GenerateStructuredAsync<RealtimeAssessmentService.AssessmentAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RealtimeAssessmentService.AssessmentAiResponse
            {
                Message = "Still elevated.",
                Severity = "critical",
            });
        _alerts.GetByCardiMemberAsync(_memberId, activeOnly: false).Returns(
        [
            new Alert { CardiMemberId = _memberId, AlertType = AlertType.HeartRate, IsResolved = false },
        ]);

        var assessed = await CreateSut().AssessDueMembersAsync(UtcNow);

        Assert.Equal(1, assessed);
        await _alerts.DidNotReceive().AddAsync(Arg.Any<Alert>());
        await _enqueue.DidNotReceive().EnqueueForAlertAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // A caregiver-defined heart alarm never resolves the alert it writes — its alarm may stand for
    // days — so if it counted for this cooldown it would hold the assessor shut for as long as its
    // card stayed open. It does not count.
    [Fact]
    public async Task ACaregiverAlarmsHeartAlert_DoesNotSuppressTheAssessor()
    {
        _medicalAi.GenerateStructuredAsync<RealtimeAssessmentService.AssessmentAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RealtimeAssessmentService.AssessmentAiResponse
            {
                Message = "Sharply elevated.",
                Severity = "critical",
            });
        _alerts.GetByCardiMemberAsync(_memberId, activeOnly: false).Returns(
        [
            new Alert
            {
                CardiMemberId = _memberId, AlertType = AlertType.HeartRate, IsResolved = false,
                MetricValues = $"{{\"rule\":\"{MetricAlarmEngine.CustomRule(Guid.NewGuid())}\"}}",
            },
        ]);

        var assessed = await CreateSut().AssessDueMembersAsync(UtcNow);

        Assert.Equal(1, assessed);
        await _alerts.Received(1).AddAsync(Arg.Is<Alert>(a => a.AlertType == AlertType.HeartRate));
    }

    // The converse: the assessor's ordinary hour closes its own episode, not a caregiver's alarm,
    // whose own threshold may still be breached.
    [Fact]
    public async Task AnOrdinaryWindow_LeavesACaregiverAlarmsAlertOpen()
    {
        SetupWindow(HeartRateMinutes(from: 150, to: 209, bpm: 72, jumpLast: false));
        var custom = new Alert
        {
            CardiMemberId = _memberId, AlertType = AlertType.HeartRate, IsResolved = false,
            MetricValues = $"{{\"rule\":\"{MetricAlarmEngine.CustomRule(Guid.NewGuid())}\"}}",
        };
        _alerts.GetByCardiMemberAsync(_memberId, activeOnly: false).Returns([custom]);

        await CreateSut().AssessDueMembersAsync(UtcNow);

        Assert.False(custom.IsResolved);
    }

    // Two overlapping executions can both assess the same window — the Exists probe is not
    // atomic with the inference — but the upsert reports which one actually inserted, and only
    // the inserter may alert. The loser must route nothing, or one episode pages twice.
    [Fact]
    public async Task WhenAConcurrentPassWonTheInsert_NoSecondAlertIsRaised()
    {
        _medicalAi.GenerateStructuredAsync<RealtimeAssessmentService.AssessmentAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RealtimeAssessmentService.AssessmentAiResponse
            {
                Message = "Sharply elevated.",
                Severity = "critical",
            });
        _assessments.UpsertAsync(Arg.Any<RealtimeAssessment>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var assessed = await CreateSut().AssessDueMembersAsync(UtcNow);

        Assert.Equal(1, assessed);
        await _alerts.DidNotReceive().AddAsync(Arg.Any<Alert>());
        await _enqueue.DidNotReceive().EnqueueForAlertAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ADeletedUnresolvedHeartRateAlert_SuppressesANewOne()
    {
        _medicalAi.GenerateStructuredAsync<RealtimeAssessmentService.AssessmentAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RealtimeAssessmentService.AssessmentAiResponse
            {
                Message = "Still elevated.",
                Severity = "critical",
            });
        _alerts.GetByCardiMemberAsync(_memberId, activeOnly: false).Returns(
        [
            new Alert
            {
                CardiMemberId = _memberId, AlertType = AlertType.HeartRate, IsResolved = false,
                IsActive = false,
            },
        ]);

        var assessed = await CreateSut().AssessDueMembersAsync(UtcNow);

        Assert.Equal(1, assessed);
        await _alerts.DidNotReceive().AddAsync(Arg.Any<Alert>());
        await _enqueue.DidNotReceive().EnqueueForAlertAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnOrdinaryWindow_ResolvesADeletedHeartRateAlert()
    {
        SetupWindow(HeartRateMinutes(from: 150, to: 209, bpm: 72, jumpLast: false));
        var deleted = new Alert
        {
            CardiMemberId = _memberId, AlertType = AlertType.HeartRate, IsResolved = false,
            IsActive = false,
        };
        _alerts.GetByCardiMemberAsync(_memberId, activeOnly: false).Returns([deleted]);

        var assessed = await CreateSut().AssessDueMembersAsync(UtcNow);

        Assert.Equal(0, assessed);
        Assert.True(deleted.IsResolved);
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task AResolvedHeartRateAlert_DoesNotSuppress()
    {
        _medicalAi.GenerateStructuredAsync<RealtimeAssessmentService.AssessmentAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RealtimeAssessmentService.AssessmentAiResponse
            {
                Message = "Elevated again.",
                Severity = "critical",
            });
        _alerts.GetByCardiMemberAsync(_memberId, activeOnly: false).Returns(
        [
            new Alert { CardiMemberId = _memberId, AlertType = AlertType.HeartRate, IsResolved = true },
        ]);

        await CreateSut().AssessDueMembersAsync(UtcNow);

        await _alerts.Received(1).AddAsync(Arg.Any<Alert>());
        await _enqueue.Received(1).EnqueueForAlertAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // Fail safe in both directions: the model cannot page a family by deviating from the schema's
    // suggested vocabulary, but what it actually said is still stored — a null Severity with a
    // non-null RawSeverity is the visible record that the mapping, not the storage, rejected it.
    [Fact]
    public async Task AnUnmappableSeverityWord_IsStored_ButRoutesNowhere()
    {
        _medicalAi.GenerateStructuredAsync<RealtimeAssessmentService.AssessmentAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RealtimeAssessmentService.AssessmentAiResponse
            {
                Message = "Everything considered, the picture defies a simple label.",
                Severity = "unclear",
            });

        var assessed = await CreateSut().AssessDueMembersAsync(UtcNow);

        Assert.Equal(1, assessed);
        await _assessments.Received(1).UpsertAsync(
            Arg.Is<RealtimeAssessment>(a => a.Severity == null && a.RawSeverity == "unclear"),
            Arg.Any<CancellationToken>());
        await _alerts.DidNotReceive().AddAsync(Arg.Any<Alert>());
        await _enqueue.DidNotReceive().EnqueueForAlertAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // The natural key is the cost control: a window only moves when new minutes land, so an
    // already-assessed window must never reach the model again.
    [Fact]
    public async Task AnAlreadyAssessedWindow_CostsNoInference()
    {
        _assessments.ExistsAsync(_memberId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var assessed = await CreateSut().AssessDueMembersAsync(UtcNow);

        Assert.Equal(0, assessed);
        await _medicalAi.DidNotReceive().GenerateStructuredAsync<RealtimeAssessmentService.AssessmentAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AThinWindow_IsNotAssessed()
    {
        // 30 minutes of data — under the 45-minute coverage floor.
        SetupWindow(HeartRateMinutes(from: 180, to: 209, bpm: 72));

        var assessed = await CreateSut().AssessDueMembersAsync(UtcNow);

        Assert.Equal(0, assessed);
        await _medicalAi.DidNotReceive().GenerateStructuredAsync<RealtimeAssessmentService.AssessmentAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InteriorGaps_AreTolerated_WhenCoverageHolds()
    {
        // 60-minute window with every 4th minute missing: 45 covered minutes exactly.
        var series = HeartRateMinutes(from: 150, to: 209, bpm: 72);
        for (var i = 150; i <= 209; i += 4)
            series[i] = null;
        SetupWindow(series);

        var assessed = await CreateSut().AssessDueMembersAsync(UtcNow);

        Assert.Equal(1, assessed);
    }

    [Fact]
    public async Task NoHeartRateSeries_MeansNoAssessment()
    {
        SetupWindowSeries(new Dictionary<GranularMetric, float?[]>
        {
            [GranularMetric.Steps] = HeartRateMinutes(from: 150, to: 209, bpm: 30),
        });

        var assessed = await CreateSut().AssessDueMembersAsync(UtcNow);

        Assert.Equal(0, assessed);
    }

    [Fact]
    public async Task DataTooOldForAFullWindow_MeansNoAssessment()
    {
        // The latest minute is index 40 — no 60-minute window can end there.
        SetupWindow(HeartRateMinutes(from: 0, to: 40, bpm: 72));

        var assessed = await CreateSut().AssessDueMembersAsync(UtcNow);

        Assert.Equal(0, assessed);
    }

    [Fact]
    public async Task APausedMember_IsNotAssessed()
    {
        var paused = Member();
        paused.MonitoringPausedUntil = UtcNow.AddDays(1);
        _members.GetByIdAsync(_memberId).Returns(paused);

        var assessed = await CreateSut().AssessDueMembersAsync(UtcNow);

        Assert.Equal(0, assessed);
    }

    [Fact]
    public async Task DisabledRealtimeHeartRateRule_IsNotAssessed()
    {
        _alertPreferences.GetByCardiMemberIdAsync(_memberId).Returns(new AlertPreference
        {
            CardiMemberId = _memberId,
            DisabledRules = """["realtime_hr"]""",
        });

        var assessed = await CreateSut().AssessDueMembersAsync(UtcNow);

        Assert.Equal(0, assessed);
        await _granular.DidNotReceive().GetWindowAsync(
            Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _medicalAi.DidNotReceive().GenerateStructuredAsync<RealtimeAssessmentService.AssessmentAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OneMemberFailure_DoesNotCostTheRestThePass()
    {
        var otherId = Guid.NewGuid();
        _members.GetActiveIdsWithActivitySinceAsync(Arg.Any<DateOnly>()).Returns([otherId, _memberId]);
        _members.GetByIdAsync(otherId).Throws(new InvalidOperationException("db hiccup"));

        var assessed = await CreateSut().AssessDueMembersAsync(UtcNow);

        Assert.Equal(1, assessed);
    }

    [Fact]
    public async Task ThePrompt_CarriesTheYardsticks_AndContext_ButNeverTheName()
    {
        var steps = new float?[RangeMinutes];
        for (var i = 150; i <= 209; i++)
            steps[i] = 10f;
        SetupWindow(HeartRateMinutes(from: 150, to: 209, bpm: 72), steps);
        string? prompt = null;
        _medicalAi.GenerateStructuredAsync<RealtimeAssessmentService.AssessmentAiResponse>(
                Arg.Do<string>(p => prompt = p), Arg.Any<CancellationToken>())
            .Returns(new RealtimeAssessmentService.AssessmentAiResponse { Message = "Fine.", Severity = "low" });

        await CreateSut().AssessDueMembersAsync(UtcNow);

        Assert.NotNull(prompt);
        Assert.Contains("exactly one of critical, high, medium, or low", prompt);
        Assert.Contains("scores under 3 are ordinary", prompt);
        Assert.Contains("Write as a caregiver would", prompt);
        Assert.DoesNotContain("heart patient", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("elevated heart rate during steps", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Deviation score", prompt);
        Assert.Contains("Steps this hour: 600", prompt);
        Assert.Contains("SpO2 this hour: not measured", prompt);
        Assert.Contains("On beta blockers.", prompt);
        Assert.DoesNotContain("Margaret", prompt);
    }

    [Fact]
    public async Task ANonConsentedMember_NeverQueriesEnvironmentalReadings()
    {
        // The consent gate applies before the query, not after — every non-consented member
        // (the common case, every pass) must skip the roundtrip entirely, not query and find
        // nothing. _members.GetByIdAsync(_memberId) already returns a member with
        // EnvironmentalContextConsentGranted = false by default (Member()'s default).
        await CreateSut().AssessDueMembersAsync(UtcNow);

        await _environmentalReadings.DidNotReceiveWithAnyArgs()
            .GetLatestAsync(default, default);
    }

    [Fact]
    public async Task AConsentedMembersRecentReading_ReachesThePrompt()
    {
        var consentedMember = Member();
        consentedMember.EnvironmentalContextConsentGranted = true;
        _members.GetByIdAsync(_memberId).Returns(consentedMember);
        _environmentalReadings.GetLatestAsync(_memberId, Arg.Any<CancellationToken>()).Returns(new EnvironmentalReading
        {
            CardiMemberId = _memberId,
            SessionStartUtc = UtcNow.AddHours(-1),
            SessionEndUtc = UtcNow.AddMinutes(-45),
            TemperatureCelsius = 31.0,
            AirQualityCategory = "Moderate",
            GeneratedAtUtc = UtcNow.AddMinutes(-40),
        });
        string? prompt = null;
        _medicalAi.GenerateStructuredAsync<RealtimeAssessmentService.AssessmentAiResponse>(
                Arg.Do<string>(p => prompt = p), Arg.Any<CancellationToken>())
            .Returns(new RealtimeAssessmentService.AssessmentAiResponse { Message = "Fine.", Severity = "low" });

        await CreateSut().AssessDueMembersAsync(UtcNow);

        Assert.NotNull(prompt);
        Assert.Contains("31°C", prompt);
        Assert.Contains("air quality Moderate", prompt);
    }
}
