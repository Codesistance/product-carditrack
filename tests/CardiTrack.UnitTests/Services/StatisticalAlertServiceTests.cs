using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins the engine's orchestration guarantees: no established baseline means total silence
/// (provisional never alerts), cooldowns are scoped to the family's remedy — rule-scoped
/// where remedies differ, type-scoped for the heart — and one day's data produces at most one
/// alert per rule regardless of the 15-minute cadence.
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

        // Defaults: one active London-anchored member with an established baseline, a sharp
        // step decline yesterday, and no standing alerts.
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
    }

    private CardiMember Member() => new()
    {
        Id = _memberId,
        Name = "Margaret Doe",
        DateOfBirth = new DateOnly(1948, 3, 2),
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

    private StatisticalAlertService CreateSut() =>
        new(_unitOfWork, Substitute.For<IDispatchService>(),
            NullLogger<StatisticalAlertService>.Instance);

    [Fact]
    public async Task ASharpDecline_RaisesOneYellowInactivityAlert_WithItsRuleMarker()
    {
        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(1, raised);
        await _alerts.Received(1).AddAsync(Arg.Is<Alert>(a =>
            a.CardiMemberId == _memberId
            && a.AlertType == AlertType.Inactivity
            && a.Severity == AlertSeverity.Yellow
            && a.MetricValues!.Contains("\"rule\":\"activity_decline\"")));
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    // Provisional-never-alerts, enforced by what is fetched: no 30-day baseline, no rules —
    // and no wasted reads.
    [Fact]
    public async Task NoEstablishedBaseline_MeansTotalSilence()
    {
        _baselines.GetLatestByCardiMemberAsync(_memberId, 30).Returns((PatternBaseline?)null);

        var raised = await CreateSut().EvaluateAsync(UtcNow);

        Assert.Equal(0, raised);
        await _activityLogs.DidNotReceive().GetByCardiMemberAndDateRangeAsync(
            Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>());
    }

    [Fact]
    public async Task SeveralRulesCanFireTogether_InOneSave()
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
    }

    [Fact]
    public async Task AnUnresolvedAlertOfTheSameRule_Suppresses()
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
    // 15 minutes later just because the card left the list.
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

    // A deleted statistical alert must not latch the rule forever — this engine does not
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
    // The engine used to read yesterday's row, which meant a poor night showed POOR on the
    // dashboard all day while the alert could only arrive tomorrow.

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
    // row, or losing its candidate to the shared HeartRate cooldown.
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
                CardiMemberId = _memberId, Date = Yesterday.AddDays(1), OvernightBreathingRate = 17m,
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
        // point of the pairing.
        Assert.Equal(2, raised);
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
                CardiMemberId = _memberId, Date = Yesterday, Steps = 6000, HeartRateVariabilityMs = 26m,
            },
            new ActivityLog
            {
                CardiMemberId = _memberId, Date = Yesterday.AddDays(1), HeartRateVariabilityMs = 25m,
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
