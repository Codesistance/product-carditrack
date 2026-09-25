using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The evidence block is the one part of an alert that is authored rather than generated, so what
/// these pin is not phrasing but provenance: that every shipped rule can explain itself, that the
/// figure quoted is the margin the producer actually fired on, and that a row we cannot explain
/// honestly says nothing at all.
/// </summary>
public class AlertEvidenceComposerTests
{
    private readonly DateOnly _today = new(2026, 9, 20);
    private readonly Guid _memberId = Guid.NewGuid();

    [Theory]
    [InlineData(StatisticalAlertRules.ActivityDeclineRule)]
    [InlineData(StatisticalAlertRules.IrregularSleepRule)]
    [InlineData(StatisticalAlertRules.ElevatedHeartRateRule)]
    [InlineData(StatisticalAlertRules.NoMorningActivityRule)]
    [InlineData(StatisticalAlertRules.LongTermTrendRule)]
    [InlineData(StatisticalAlertRules.HeartRateVariabilityDropRule)]
    [InlineData(StatisticalAlertRules.OvernightBreathingUpRule)]
    [InlineData(StatisticalAlertRules.ElevatedZoneWithoutMovementRule)]
    [InlineData(StatisticalAlertRules.DaytimeInactivityBlockRule)]
    [InlineData(AlertDetailComposer.RealtimeHeartRateRule)]
    [InlineData(AlertDetailComposer.DeviceSilenceRule)]
    public void EveryShippedRuleExplainsItself(string rule)
    {
        var detail = Compose($$"""{"rule":"{{rule}}"}""");

        Assert.NotNull(detail.Evidence);
        Assert.NotEmpty(detail.Evidence!.WhyLine);

        // The name on the alert-settings screen, so the card and the toggle that silences it are
        // visibly the same rule.
        Assert.Equal(AlertRuleCatalogue.Find(rule)!.Title, detail.Evidence.RuleLabel);
    }

    [Fact]
    public void TheThresholdQuotedIsTheMarginThatFired_NotTheRuleConstant()
    {
        // Two standard deviations of this member's own variability came to 12 ms, well past the
        // 15% floor. Re-deriving the floor at read time would name 7.5 ms and describe a line this
        // alert was never judged against.
        var detail = Compose(
            """
            {"rule":"hrv_drop","heartRateVariabilityMs":31.0,
             "baselineAvgHeartRateVariabilityMs":50.0,"marginMs":12.0}
            """);

        Assert.Equal("Below 38 ms, two nights running", detail.Evidence!.ThresholdLabel);
        Assert.Contains("12 ms", detail.Evidence.WhyLine);
        Assert.Contains("both of the last two nights", detail.Evidence.WhyLine);
    }

    [Fact]
    public void RestingHeartRateThresholdIsTheUsualPlusTheStampedMargin()
    {
        var detail = Compose(
            """
            {"rule":"elevated_heart_rate","restingHeartRate":71,
             "baselineAvgRestingHeartRate":62,"marginBpm":6.0}
            """);

        Assert.Equal("Above 68 bpm", detail.Evidence!.ThresholdLabel);
    }

    [Fact]
    public void ActivityDeclineNamesTheFigureThirtyPercentBelowTheirUsual()
    {
        var detail = Compose("""{"rule":"activity_decline","steps":1900,"baselineAvgSteps":5000}""");

        Assert.Equal("Below 3,500 steps", detail.Evidence!.ThresholdLabel);
    }

    [Fact]
    public void AShortNightNamesTheFloorItFellBelow()
    {
        var detail = Compose(
            """
            {"rule":"irregular_sleep","night":"2026-09-19","sleepMinutes":260,
             "baselineAvgSleepMinutes":450,"recommendedLowHours":7,"recommendedHighHours":8}
            """);

        Assert.Equal("Shorter than 5.3 h", detail.Evidence!.ThresholdLabel);
    }

    [Fact]
    public void ALongNightNamesTheCeilingItPassed_NotAFloorItNeverApproached()
    {
        // The rule is symmetric going in but not coming out: a longer night counts only once it
        // passes the ceiling recommended at their age. Quoting "shorter than" here would print
        // the opposite of the thing that raised the alert.
        var detail = Compose(
            """
            {"rule":"irregular_sleep","night":"2026-09-19","sleepMinutes":620,
             "baselineAvgSleepMinutes":450,"recommendedLowHours":7,"recommendedHighHours":8}
            """);

        Assert.Equal("Longer than 8 h, their recommended ceiling", detail.Evidence!.ThresholdLabel);
    }

    [Fact]
    public void NoMorningActivityNamesTheHourTheGraceRunsOut()
    {
        var detail = Compose("""{"rule":"no_morning_activity","typicalWakeTime":"07:00"}""");

        Assert.Equal("No steps by 09:00", detail.Evidence!.ThresholdLabel);
        // The distinction the rule is built on has to survive into the copy: a measured zero is a
        // finding, a missing reading is a different alert entirely.
        Assert.Contains("measured a zero", detail.Evidence.WhyLine);
    }

    [Fact]
    public void AMarginlessRowGetsASentenceButNoFigureItCannotStandBehind()
    {
        // An older row, stamped before the rule carried its margin. The rule's constant is still
        // true and is named; the member-specific line is not invented.
        var detail = Compose("""{"rule":"elevated_heart_rate","restingHeartRate":71}""");

        Assert.NotNull(detail.Evidence);
        Assert.Null(detail.Evidence!.ThresholdLabel);
        Assert.Contains("5 bpm", detail.Evidence.WhyLine);
    }

    [Fact]
    public void ARowThisBuildCannotExplainSaysNothing()
    {
        // A markerless legacy alert, and a rule from a build that is not this one. An evidence card
        // that shrugs still reads as a claim, so neither gets one.
        Assert.Null(Compose("""{"steps":1200}""").Evidence);
        Assert.Null(Compose("""{"rule":"someone_elses_rule"}""").Evidence);
        Assert.Null(Compose(null).Evidence);
    }

    [Fact]
    public void TheBaselineWindowIsReportedOnlyWhenOneWasRead()
    {
        var withBaseline = Compose(
            """{"rule":"activity_decline","steps":1900}""",
            new PatternBaseline { CardiMemberId = Guid.NewGuid(), PeriodDays = 30, AvgSteps = 5000 });

        Assert.Equal(30, withBaseline.Evidence!.BaselinePeriodDays);
        Assert.Null(Compose("""{"rule":"activity_decline","steps":1900}""").Evidence!.BaselinePeriodDays);
    }

    /// <summary>
    /// The window is a claim about the sentence beside it, not about the member. Six rules measure
    /// against a learned usual and five do not — the trend is week-over-week, both pairing rules
    /// and device silence quote fixed thresholds, the realtime rule judges an hour against itself
    /// — and the card appends "measured against their last 30 days" to whatever this carries. A
    /// baseline being available for the member says nothing about whether their line used one.
    /// </summary>
    [Theory]
    [InlineData(StatisticalAlertRules.LongTermTrendRule)]
    [InlineData(StatisticalAlertRules.ElevatedZoneWithoutMovementRule)]
    [InlineData(StatisticalAlertRules.DaytimeInactivityBlockRule)]
    [InlineData(AlertDetailComposer.RealtimeHeartRateRule)]
    [InlineData(AlertDetailComposer.DeviceSilenceRule)]
    public void ARuleThatMeasuresAgainstNoUsualClaimsNoWindow(string rule)
    {
        var detail = Compose($$"""{"rule":"{{rule}}"}""", Baseline());

        Assert.NotNull(detail.Evidence);
        Assert.NotEmpty(detail.Evidence!.WhyLine);
        Assert.Null(detail.Evidence.BaselinePeriodDays);
    }

    [Theory]
    [InlineData(StatisticalAlertRules.ActivityDeclineRule)]
    [InlineData(StatisticalAlertRules.IrregularSleepRule)]
    [InlineData(StatisticalAlertRules.ElevatedHeartRateRule)]
    [InlineData(StatisticalAlertRules.NoMorningActivityRule)]
    [InlineData(StatisticalAlertRules.HeartRateVariabilityDropRule)]
    [InlineData(StatisticalAlertRules.OvernightBreathingUpRule)]
    public void ARuleThatMeasuresAgainstTheirUsualNamesTheWindow(string rule)
    {
        var detail = Compose($$"""{"rule":"{{rule}}"}""", Baseline());

        Assert.Equal(30, detail.Evidence!.BaselinePeriodDays);
    }

    [Fact]
    public void ACaregiversOwnAlarmClaimsNoWindowEither()
    {
        // Their level, their window. A baseline read for the member is beside the point.
        var detail = Compose(
            """
            {"rule":"custom:3f2a5b1c-0000-4000-8000-000000000001","alarmName":"Nan's resting rate",
             "configuredThreshold":110,"effectiveThreshold":110,
             "condition":"Average heart rate is above 110 bpm."}
            """,
            Baseline());

        Assert.Null(detail.Evidence!.BaselinePeriodDays);
    }

    private static PatternBaseline Baseline() =>
        new() { CardiMemberId = Guid.NewGuid(), PeriodDays = 30, AvgSteps = 5000 };

    [Fact]
    public void ACaregiversOwnAlarmIsReadBackToThem()
    {
        var detail = Compose(
            """
            {"rule":"custom:3f2a5b1c-0000-4000-8000-000000000001","alarmId":"3f2a5b1c-0000-4000-8000-000000000001",
             "alarmName":"Nan's resting rate","metric":"HeartRate","statistic":"Average","comparison":"GreaterThan",
             "thresholdKind":"Absolute","configuredThreshold":110,"effectiveThreshold":110,"observedValue":124,
             "condition":"Average heart rate is above 110 bpm over 10 minutes, on 2 of 3 readings, while they are still."}
            """);

        Assert.Equal("Nan's resting rate", detail.Evidence!.RuleLabel);
        Assert.StartsWith("You asked to be told when average heart rate is above 110 bpm", detail.Evidence.WhyLine);
        Assert.Equal("110 bpm", detail.Evidence.ThresholdLabel);

        // The card that had nothing at all for a custom alarm before this.
        Assert.NotNull(detail.Comparison);
        Assert.Equal("Measured", detail.Comparison!.CurrentLabel);
        Assert.Equal("124 bpm", detail.Comparison.CurrentValue);
        Assert.Equal("Your level", detail.Comparison.NormalLabel);
        Assert.Equal("110 bpm", detail.Comparison.NormalValue);
        Assert.Equal("13% above the level you set", detail.Comparison.ChangeLabel);
    }

    [Fact]
    public void AnAlarmConditionOpeningOnAMetricNameKeepsItsCapitals()
    {
        var detail = Compose(
            """
            {"rule":"custom:3f2a5b1c-0000-4000-8000-000000000002","alarmName":"Oxygen","metric":"SpO2",
             "effectiveThreshold":92,"observedValue":89,
             "condition":"SpO2 average is below 92% over 10 minutes, on 2 of 3 readings."}
            """);

        Assert.Contains("when SpO2 average is below", detail.Evidence!.WhyLine);
        Assert.Equal("92 %", detail.Evidence.ThresholdLabel);
    }

    [Fact]
    public void DeviceSilenceComparesTheQuietStretchAgainstTheLimit()
    {
        // Four hours quiet against a three-hour limit: the card that was null for this rule.
        var lastData = _today.ToDateTime(new TimeOnly(8, 22), DateTimeKind.Utc);
        var detail = Compose(
            $$"""
            {"rule":"device_silence","lastDataUtc":"{{lastData:yyyy-MM-ddTHH:mm:ssZ}}","thresholdMinutes":180}
            """);

        Assert.NotNull(detail.Comparison);
        Assert.Equal("Quiet for", detail.Comparison!.CurrentLabel);
        Assert.Equal("4 h", detail.Comparison.CurrentValue);
        Assert.Equal("Alerts after", detail.Comparison.NormalLabel);
        Assert.Equal("3 h", detail.Comparison.NormalValue);
        Assert.Equal("33% above the quiet limit", detail.Comparison.ChangeLabel);

        // And it stays a statement about the watch, not about the wearer holding still.
        Assert.Contains("about the watch, not about them", detail.Evidence!.WhyLine);
    }

    [Fact]
    public void DeviceSilenceWithNoLastReadingStillNamesTheLimit()
    {
        var detail = Compose("""{"rule":"device_silence","thresholdMinutes":180}""");

        Assert.Equal("—", detail.Comparison!.CurrentValue);
        Assert.Equal("3 h", detail.Comparison.NormalValue);
        Assert.Null(detail.Comparison.ChangeLabel);
    }

    /// <summary>
    /// Through <see cref="AlertDetailComposer.Compose"/> rather than the evidence composer alone:
    /// the wiring is half of what these are protecting, and an evidence block that is never hung
    /// off the response is worth nothing.
    /// </summary>
    private AlertDetailResponse Compose(string? metricValues, PatternBaseline? baseline = null)
    {
        var alert = new Alert
        {
            Id = Guid.NewGuid(),
            CardiMemberId = _memberId,
            AlertType = AlertType.PatternBreak,
            Severity = AlertSeverity.Yellow,
            Title = "Test",
            Message = "Something changed.",
            TriggeredDate = _today.ToDateTime(new TimeOnly(12, 22), DateTimeKind.Utc),
            MetricValues = metricValues,
            IsActive = true,
        };

        return AlertDetailComposer.Compose(
            alert,
            new CardiMember { Id = _memberId, FirstName = "Test", LastName = "Member" },
            null,
            [],
            _today,
            null,
            baseline);
    }
}
