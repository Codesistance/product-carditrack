using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Services;

public class AlertDetailComposerTests
{
    private readonly DateOnly _today = new(2026, 8, 14);
    private readonly Guid _memberId = Guid.NewGuid();

    [Fact]
    public void ReadRule_ReturnsTheProducerStamp()
    {
        Assert.Equal(
            StatisticalAlertRules.ActivityDeclineRule,
            AlertDetailComposer.ReadRule("""{"rule":"activity_decline","steps":2500}"""));
        Assert.Null(AlertDetailComposer.ReadRule(null));
        Assert.Null(AlertDetailComposer.ReadRule("not-json"));
    }

    [Theory]
    [InlineData(StatisticalAlertRules.ActivityDeclineRule, 14)]
    [InlineData(StatisticalAlertRules.NoMorningActivityRule, 14)]
    [InlineData(StatisticalAlertRules.LongTermTrendRule, 28)]
    [InlineData(StatisticalAlertRules.ElevatedHeartRateRule, 7)]
    [InlineData(StatisticalAlertRules.IrregularSleepRule, 14)]
    [InlineData(AlertDetailComposer.DeviceSilenceRule, 0)]
    [InlineData(AlertDetailComposer.RealtimeHeartRateRule, 0)]
    [InlineData(null, 0)]
    public void DailyLogDays_IsTheWindowForThatRuleOnly(string? rule, int days) =>
        Assert.Equal(days, AlertDetailComposer.DailyLogDays(rule));

    [Fact]
    public void NeedsGranular_OnlyForTheRealtimeHeartRule()
    {
        Assert.True(AlertDetailComposer.NeedsGranular(AlertDetailComposer.RealtimeHeartRateRule));
        Assert.False(AlertDetailComposer.NeedsGranular(StatisticalAlertRules.ActivityDeclineRule));
        Assert.False(AlertDetailComposer.NeedsGranular(StatisticalAlertRules.ElevatedHeartRateRule));
    }

    [Fact]
    public void ActivityDecline_PlotsStepsNotHeartRate()
    {
        var alert = MakeAlert(
            AlertType.Inactivity,
            """{"rule":"activity_decline","steps":2500,"baselineAvgSteps":5000}""");
        var logs = new[]
        {
            Log(_today.AddDays(-1), steps: 2500, restingHr: 88, sleepMinutes: 400),
            Log(_today.AddDays(-2), steps: 4800, restingHr: 70, sleepMinutes: 450),
        };

        var detail = AlertDetailComposer.Compose(alert, Member(), null, logs, _today, null, null);

        Assert.Equal("activity_decline", detail.Rule);
        Assert.NotNull(detail.Chart);
        Assert.Equal("steps", detail.Chart!.Metric);
        Assert.Equal(14, detail.Chart.Series.Count);
        Assert.Equal(2500, detail.Chart.Value);
        Assert.Equal(5000, detail.Chart.Baseline);
        Assert.DoesNotContain(detail.Chart.Series, p => p.Value == 88);
        Assert.Equal("Yesterday", detail.Comparison!.CurrentLabel);
        Assert.Contains("2,500", detail.Comparison.CurrentValue);
        Assert.Equal("50% below usual", detail.Comparison.ChangeLabel);
    }

    [Fact]
    public void ElevatedHeartRate_PlotsRestingHrNotSteps()
    {
        var alert = MakeAlert(
            AlertType.HeartRate,
            """{"rule":"elevated_heart_rate","restingHeartRate":88,"baselineAvgRestingHeartRate":68}""");
        var logs = new[]
        {
            Log(_today.AddDays(-1), steps: 9000, restingHr: 88),
            Log(_today.AddDays(-2), steps: 8000, restingHr: 70),
        };

        var detail = AlertDetailComposer.Compose(alert, Member(), null, logs, _today, null, null);

        Assert.Equal("restingHeartRate", detail.Chart!.Metric);
        Assert.Equal(7, detail.Chart.Series.Count);
        Assert.Equal(88, detail.Chart.Value);
        Assert.DoesNotContain(detail.Chart.Series, p => p.Value == 9000);
        Assert.Contains("88", detail.Comparison!.CurrentValue);
        Assert.Contains("bpm", detail.Comparison.CurrentValue);
    }

    [Fact]
    public void IrregularSleep_PlotsHoursNotSteps()
    {
        var alert = MakeAlert(
            AlertType.Sleep,
            """{"rule":"irregular_sleep","sleepMinutes":240,"baselineAvgSleepMinutes":420}""");
        var logs = new[] { Log(_today, steps: 5000, sleepMinutes: 240) };

        var detail = AlertDetailComposer.Compose(alert, Member(), null, logs, _today, null, null);

        Assert.Equal("sleep", detail.Chart!.Metric);
        Assert.Equal(4m, detail.Chart.Value);
        Assert.Equal(7m, detail.Chart.Baseline);
        Assert.Contains("hours", detail.Comparison!.CurrentValue);
    }

    [Fact]
    public void DeviceSilence_HasNoChart()
    {
        var alert = MakeAlert(
            AlertType.Inactivity,
            """{"rule":"device_silence","lastDataUtc":"2026-08-14T08:00:00Z"}""");

        var detail = AlertDetailComposer.Compose(
            alert, Member(), null, [Log(_today, steps: 4000)], _today, null, null);

        Assert.Null(detail.Chart);
        Assert.Null(detail.Comparison);
        Assert.Equal(new DateTime(2026, 8, 14, 8, 0, 0, DateTimeKind.Utc), detail.LastDataAt);
    }

    [Fact]
    public void RealtimeHeartRate_UsesTheGranularHourNotDailyLogs()
    {
        var start = new DateTime(2026, 8, 14, 10, 0, 0, DateTimeKind.Utc);
        var samples = new float?[60];
        samples[0] = 70;
        samples[59] = 92;
        var window = new GranularWindow
        {
            CardiMemberId = _memberId,
            FromUtc = start,
            ToUtc = start.AddHours(1),
            MinuteSeries = new Dictionary<GranularMetric, float?[]>
            {
                [GranularMetric.HeartRate] = samples,
                [GranularMetric.Steps] = Enumerable.Repeat<float?>(4000, 60).ToArray(),
            },
        };
        var alert = MakeAlert(
            AlertType.HeartRate,
            """{"rule":"realtime_hr","hrTrendLast":92,"windowStartUtc":"2026-08-14T10:00:00Z","windowEndUtc":"2026-08-14T11:00:00Z"}""");

        var detail = AlertDetailComposer.Compose(
            alert, Member(), null, [Log(_today, steps: 9000, restingHr: 60)], _today, window,
            new PatternBaseline { AvgRestingHeartRate = 68 });

        Assert.Equal("heartRate", detail.Chart!.Metric);
        Assert.Equal("This hour", detail.Chart.WindowLabel);
        Assert.Equal(92, detail.Chart.Value);
        Assert.DoesNotContain(detail.Chart.Series, p => p.Value == 9000);
        Assert.DoesNotContain(detail.Chart.Series, p => p.Value == 4000);
    }

    [Fact]
    public void RealtimeHeartRate_KeepsTheClosingMinuteWhenDownsampling()
    {
        var start = new DateTime(2026, 8, 14, 10, 0, 0, DateTimeKind.Utc);
        var samples = new float?[AlertDetailComposer.GranularMaxPoints * 2];
        samples[0] = 70;
        samples[^2] = 80;
        samples[^1] = 99;
        var window = new GranularWindow
        {
            CardiMemberId = _memberId,
            FromUtc = start,
            ToUtc = start.AddHours(1),
            MinuteSeries = new Dictionary<GranularMetric, float?[]>
            {
                [GranularMetric.HeartRate] = samples,
            },
        };
        var alert = MakeAlert(
            AlertType.HeartRate,
            """{"rule":"realtime_hr","hrTrendLast":99,"windowStartUtc":"2026-08-14T10:00:00Z","windowEndUtc":"2026-08-14T11:00:00Z"}""");

        var detail = AlertDetailComposer.Compose(alert, Member(), null, [], _today, window, null);

        Assert.Equal(99, detail.Chart!.Value);
        Assert.Equal(99, detail.Chart.Series[^1].Value);
        Assert.True(detail.Chart.Series.Count <= AlertDetailComposer.GranularMaxPoints);
    }

    [Fact]
    public void GranularBounds_AlignToWholeUtcHours()
    {
        var bounds = AlertDetailComposer.GranularBounds(
            """{"rule":"realtime_hr","windowStartUtc":"2026-08-14T10:07:00Z","windowEndUtc":"2026-08-14T10:51:00Z"}""");

        Assert.NotNull(bounds);
        Assert.Equal(new DateTime(2026, 8, 14, 10, 0, 0, DateTimeKind.Utc), bounds!.Value.FromUtc);
        Assert.Equal(new DateTime(2026, 8, 14, 11, 0, 0, DateTimeKind.Utc), bounds.Value.ToUtc);
    }

    [Fact]
    public void Compose_CarriesTheAcknowledgerNameAndMemberPhones()
    {
        var alert = MakeAlert(AlertType.Inactivity, """{"rule":"activity_decline","steps":1}""");
        alert.AcknowledgedDate = DateTime.UtcNow;
        alert.AcknowledgedByUserId = Guid.NewGuid();

        var detail = AlertDetailComposer.Compose(
            alert,
            Member(),
            new User { Name = "Sarah Chen" },
            [],
            _today,
            null,
            null);

        Assert.Equal("Sarah Chen", detail.AcknowledgedByName);
        Assert.Equal("acknowledged", detail.Status);
        Assert.Equal("+15550001111", detail.Phone);
        Assert.Equal("+15550002222", detail.EmergencyContactPhone);
    }

    [Fact]
    public void NoMorning_NamesLastMeasuredStepsDay()
    {
        var alert = MakeAlert(
            AlertType.PatternBreak,
            """{"rule":"no_morning_activity","typicalWakeTime":"07:00"}""");
        var logs = new[]
        {
            Log(_today, steps: 0),
            Log(_today.AddDays(-1), steps: 6120),
        };

        var detail = AlertDetailComposer.Compose(alert, Member(), null, logs, _today, null, null);

        Assert.Equal(_today.AddDays(-1), detail.LastActivityOn);
        Assert.Equal("07:00", detail.TypicalWakeTime);
        Assert.Equal("steps", detail.Chart!.Metric);
    }

    private Alert MakeAlert(AlertType type, string metricValues) => new()
    {
        Id = Guid.NewGuid(),
        CardiMemberId = _memberId,
        AlertType = type,
        Severity = AlertSeverity.Yellow,
        Title = "Test",
        Message = "Something changed.",
        TriggeredDate = DateTime.UtcNow,
        MetricValues = metricValues,
        IsActive = true,
    };

    private CardiMember Member() => new()
    {
        Id = _memberId,
        Name = "Margaret Doe",
        Phone = "+15550001111",
        EmergencyContactName = "Jane",
        EmergencyContactPhone = "+15550002222",
        IsActive = true,
    };

    private ActivityLog Log(
        DateOnly date, int? steps = null, int? restingHr = null, int? sleepMinutes = null) => new()
    {
        CardiMemberId = _memberId,
        Date = date,
        Steps = steps,
        RestingHeartRate = restingHr,
        SleepMinutes = sleepMinutes,
    };
}
