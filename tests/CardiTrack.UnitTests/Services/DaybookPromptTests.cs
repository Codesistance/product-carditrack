using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins the daybook entry's readings block and the two guards its register turns on: what a precise
/// word is allowed to be, and where the line to diagnosis sits.
/// </summary>
public class DaybookPromptTests
{
    private const int AdultAge = 60;
    private static readonly DateOnly Reviewed = new(2026, 8, 17);

    private static ActivityLog Log(
        int? steps = null,
        int? activeMinutes = null,
        int? restingHr = null,
        int? minHr = null,
        int? maxHr = null,
        int? sleepMinutes = null,
        int? sleepEfficiency = null,
        int? deep = null,
        int? rem = null,
        decimal? spo2 = null,
        decimal? spo2Min = null,
        decimal? spo2Max = null,
        decimal? breathing = null,
        decimal? temperature = null,
        decimal? temperatureBaseline = null) => new()
    {
        Date = Reviewed,
        Steps = steps,
        ActiveMinutes = activeMinutes,
        RestingHeartRate = restingHr,
        MinHeartRate = minHr,
        MaxHeartRate = maxHr,
        SleepMinutes = sleepMinutes,
        SleepEfficiency = sleepEfficiency,
        DeepSleepMinutes = deep,
        RemSleepMinutes = rem,
        SpO2Average = spo2,
        SpO2Min = spo2Min,
        SpO2Max = spo2Max,
        BreathingRate = breathing,
        Temperature = temperature,
        TemperatureBaseline = temperatureBaseline,
    };

    private static PatternBaseline Baseline() => new()
    {
        PeriodDays = 30,
        AvgSteps = 6000,
        AvgActiveMinutes = 32,
        AvgRestingHeartRate = 58,
        AvgSleepMinutes = 246,
        AvgSleepEfficiency = 71,
    };

    // ── The readings block ───────────────────────────────────────────────────

    /// <summary>
    /// The reading, their own usual and the published band are all computed here — the model is
    /// handed the comparison rather than asked to make it, which is the pipeline's standing rule.
    /// </summary>
    [Fact]
    public void ReadingsSection_GivesEachReading_ItsUsualAndItsPublishedBand()
    {
        var section = DaybookPrompt.ReadingsSection(
            Log(sleepMinutes: 372, restingHr: 64, spo2: 95.4m), Baseline(), AdultAge);

        Assert.Contains("total=6.2h (their usual 4.1h) [NSF recommend 7-9h]", section);
        Assert.Contains("resting=64bpm (their usual 58bpm) [AHA recommend 60-100bpm]", section);
        Assert.Contains("bloodOxygen=95.4% [WHO recommend 94-100%]", section);
    }

    /// <summary>
    /// Silence must never read as health — the one confusion this product exists to prevent. A
    /// headline reading that is absent says so in the prompt rather than being left out of it,
    /// because a section that simply omits sleep is one the model completes from nothing.
    /// </summary>
    [Fact]
    public void ReadingsSection_SaysWhenAHeadlineReadingWasNotMeasured()
    {
        var section = DaybookPrompt.ReadingsSection(Log(steps: 4200), Baseline(), AdultAge);

        Assert.Contains("total=not measured", section);
        Assert.Contains("resting=not measured", section);
    }

    /// <summary>
    /// A member still being learned has no usual, and gets none invented for them — the figures
    /// and the published bands stand on their own.
    /// </summary>
    [Fact]
    public void ReadingsSection_OmitsTheUsualClause_WhileThereIsNoBaseline()
    {
        var section = DaybookPrompt.ReadingsSection(
            Log(sleepMinutes: 372, restingHr: 64), baseline: null, AdultAge);

        Assert.DoesNotContain("their usual", section);
        Assert.Contains("total=6.2h", section);
        Assert.Contains("[NSF recommend 7-9h]", section);
    }

    /// <summary>
    /// Skin temperature is given only as a deviation from the wearer's own nightly baseline. The
    /// absolute wrist figure reads as a fever to anyone who takes it for a core temperature, which
    /// makes it the single most misreadable number the watch produces.
    /// </summary>
    [Fact]
    public void ReadingsSection_GivesSkinTemperature_OnlyAsADeviation()
    {
        var section = DaybookPrompt.ReadingsSection(
            Log(temperature: 34.9m, temperatureBaseline: 34.4m), Baseline(), AdultAge);

        Assert.Contains("skinTemperatureVsTheirOwnNightlyUsual=+0.5C", section);
        Assert.DoesNotContain("34.9", section);
    }

    /// <summary>The day is finished, and the prompt says so — every other prompt on this platform
    /// describes a day still filling up, and a running-total caveat here would be a lie.</summary>
    [Fact]
    public void ReadingsSection_StatesThatTheDayIsOver()
    {
        var section = DaybookPrompt.ReadingsSection(Log(steps: 4200), Baseline(), AdultAge);

        Assert.Contains("This day is over.", section);
        Assert.Contains("none of it is still accumulating", section);
    }

    // ── The whole day: hour tables, monitoring, conditions ───────────────────

    /// <summary>UTC+1 all year, so a conversion mistake cannot hide behind a UTC test box.</summary>
    private static readonly TimeZoneInfo PlusOne =
        TimeZoneInfo.CreateCustomTimeZone("T+1", TimeSpan.FromHours(1), "T+1", "T+1");

    private static readonly DateTime DayStartUtc = new(2026, 8, 16, 23, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime DayEndUtc = new(2026, 8, 17, 23, 0, 0, DateTimeKind.Utc);

    private static MetricRollupHourly Rollup(
        GranularMetric metric, int utcHourOffset, float min, float max, float avg, float sum = 0) => new()
    {
        Metric = metric,
        HourStartUtc = DayStartUtc.AddHours(utcHourOffset),
        Min = min,
        Max = max,
        Avg = avg,
        Sum = sum,
        SampleCount = 12,
    };

    /// <summary>
    /// The hour table is rendered in the member's local clock — the day the account is about —
    /// never the UTC hour the row is stored under.
    /// </summary>
    [Fact]
    public void IntradaySection_RendersHoursInTheMembersLocalTime()
    {
        var rollups = new List<MetricRollupHourly>
        {
            Rollup(GranularMetric.HeartRate, 7, 58, 71, 64.2f),
        };

        var section = DaybookPrompt.IntradaySection(rollups, DayStartUtc, DayEndUtc, PlusOne);

        // The row is DayStart(23:00Z)+7h = 06:00Z, which is 07:00 on the member's +1 clock.
        Assert.Contains("Heart rate: 07:00 avg 64 (58-71)", section);
    }

    [Fact]
    public void IntradaySection_SumsStepsAndKeepsOxygenDecimals()
    {
        var rollups = new List<MetricRollupHourly>
        {
            Rollup(GranularMetric.Steps, 8, 0, 210, 20.1f, sum: 1204),
            Rollup(GranularMetric.SpO2, 3, 94.0f, 96.4f, 95.4f),
        };

        var section = DaybookPrompt.IntradaySection(rollups, DayStartUtc, DayEndUtc, PlusOne);

        Assert.Contains("Steps: 08:00 1204", section);
        Assert.Contains("Blood oxygen: 03:00 avg 95.4 (94-96.4)", section);
    }

    /// <summary>
    /// A silent stretch is said as a gap — but only between hours that do have data, so the watch
    /// going quiet is stated and an unpopulated store is not mistaken for a day of silence.
    /// </summary>
    [Fact]
    public void IntradaySection_StatesTheGapBetweenCoveredHours()
    {
        var rollups = new List<MetricRollupHourly>
        {
            Rollup(GranularMetric.HeartRate, 12, 60, 70, 65),
            Rollup(GranularMetric.HeartRate, 16, 61, 69, 64),
        };

        var section = DaybookPrompt.IntradaySection(rollups, DayStartUtc, DayEndUtc, PlusOne);

        // Offsets 13-15 from the 23:00Z day start are 12:00Z-14:00Z → local 13:00 to 16:00.
        Assert.Contains("No readings at all between 13:00 and 16:00.", section);
    }

    /// <summary>An empty rollup store is absent plumbing, not a day of gaps.</summary>
    [Fact]
    public void IntradaySection_SaysNothing_WhenTheStoreIsEmpty()
    {
        Assert.Equal(string.Empty,
            DaybookPrompt.IntradaySection([], DayStartUtc, DayEndUtc, PlusOne));
    }

    /// <summary>
    /// An hour covered by any metric is not a gap — the gap claim is "no readings at all", and
    /// a steps-only hour has readings.
    /// </summary>
    [Fact]
    public void IntradaySection_AnHourCoveredByAnyMetricIsNotAGap()
    {
        var rollups = new List<MetricRollupHourly>
        {
            Rollup(GranularMetric.HeartRate, 12, 60, 70, 65),
            Rollup(GranularMetric.Steps, 13, 0, 100, 10, sum: 350),
            Rollup(GranularMetric.HeartRate, 14, 61, 69, 64),
        };

        var section = DaybookPrompt.IntradaySection(rollups, DayStartUtc, DayEndUtc, PlusOne);

        Assert.DoesNotContain("between 13:00 and 14:00", section);
    }

    [Fact]
    public void MonitoringSection_SaysTheAlertAndItsState()
    {
        var alert = new Alert
        {
            Severity = AlertSeverity.Yellow,
            Title = "Sleep was well off the usual",
            IsResolved = true,
            AcknowledgedDate = new DateTime(2026, 8, 17, 9, 0, 0, DateTimeKind.Utc),
        };

        var section = DaybookPrompt.MonitoringSection([alert], [], PlusOne);

        Assert.Contains("--- The day's monitoring ---", section);
        Assert.Contains(
            "Alert (yellow): Sleep was well off the usual — acknowledged and resolved.", section);
    }

    /// <summary>Only Yellow and above is worth the account's words — the same floor the digest's
    /// monitoring context applies. Green verdicts are the assessor confirming an ordinary hour.</summary>
    [Fact]
    public void MonitoringSection_KeepsNotableAssessments_DropsGreenOnes()
    {
        var green = new RealtimeAssessment
        {
            WindowStartUtc = DayStartUtc.AddHours(10),
            Severity = AlertSeverity.Green,
            ModelOutput = "An ordinary hour.",
        };
        var orange = new RealtimeAssessment
        {
            WindowStartUtc = DayStartUtc.AddHours(15),
            Severity = AlertSeverity.Orange,
            ModelOutput = "Heart rate well above the usual for this hour.",
        };

        var section = DaybookPrompt.MonitoringSection([], [green, orange], PlusOne);

        Assert.Contains("15:00 assessment (orange): Heart rate well above the usual", section);
        Assert.DoesNotContain("ordinary hour", section);
    }

    [Fact]
    public void MonitoringSection_SaysNothing_WhenTheDayHadNoMonitoringToReport()
    {
        Assert.Equal(string.Empty, DaybookPrompt.MonitoringSection([], [], PlusOne));
    }

    [Fact]
    public void ConditionsSection_RendersEachSessionInLocalTime()
    {
        var reading = new EnvironmentalReading
        {
            SessionStartUtc = DayStartUtc.AddHours(14).AddMinutes(5),
            SessionEndUtc = DayStartUtc.AddHours(14).AddMinutes(41),
            TemperatureCelsius = 24.3,
            WeatherCondition = "Clouds",
            RelativeHumidityPercent = 61,
            AirQualityCategory = "Moderate",
        };

        var section = DaybookPrompt.ConditionsSection([reading], PlusOne);

        Assert.Contains("--- Conditions during the day ---", section);
        Assert.Contains("14:05-14:41: 24.3°C, Clouds, humidity 61%, air quality Moderate", section);
    }

    [Fact]
    public void ConditionsSection_SaysNothing_ForNoSessionsOrEmptyReadings()
    {
        Assert.Equal(string.Empty, DaybookPrompt.ConditionsSection([], PlusOne));
        Assert.Equal(string.Empty, DaybookPrompt.ConditionsSection(
            [new EnvironmentalReading
            {
                SessionStartUtc = DayStartUtc.AddHours(2),
                SessionEndUtc = DayStartUtc.AddHours(3),
            }], PlusOne));
    }

    [Fact]
    public void DevicesLine_NamesEachReportingDeviceOnce()
    {
        var logs = new[]
        {
            new DeviceActivityLog { DataSource = DeviceType.Fitbit },
            new DeviceActivityLog { DataSource = DeviceType.Fitbit },
        };

        Assert.Equal("Readings this day came from: Fitbit.", DaybookPrompt.DevicesLine(logs));
        Assert.Equal(string.Empty, DaybookPrompt.DevicesLine([]));
    }

    // ── The line to diagnosis ────────────────────────────────────────────────

    /// <summary>
    /// Naming what was measured is description and is exactly what the register allows. None of
    /// these may be mistaken for a condition.
    /// </summary>
    [Theory]
    [InlineData("His resting heart rate was 64 bpm, four above his usual 58.")]
    [InlineData("Blood oxygen averaged 95.4%, inside the 94-100% the WHO recommend.")]
    [InlineData("She walked 4,200 steps, well under her usual 6,000.")]
    public void NamesACondition_AllowsAReadingToBeNamedPrecisely(string text)
    {
        Assert.Null(DaybookPrompt.NamesACondition(text));
    }

    /// <summary>
    /// The regulatory line: a term for what the body is doing is an inference about the person,
    /// and CardiTrack does not diagnose. The last three name no condition at all and are still
    /// refused — asserting that a reading means something about the body is the claim, whether or
    /// not it finishes the sentence.
    /// </summary>
    [Theory]
    [InlineData("His oxygen dipped overnight, which suggests sleep apnoea.")]
    [InlineData("A resting rate that low is bradycardia.")]
    [InlineData("The overnight pattern is suggestive of atrial fibrillation.")]
    [InlineData("The dip may indicate something worth investigating.")]
    [InlineData("Waking often can be a sign of a wider problem.")]
    [InlineData("The broken sleep points to a problem worth raising.")]
    public void NamesACondition_RefusesAnInferenceAboutTheBody(string text)
    {
        Assert.NotNull(DaybookPrompt.NamesACondition(text));
    }

    /// <summary>
    /// The guard must not fire on the comparison the prompt asks for. "Consistent with" is the
    /// clinical inference phrase par excellence and is still not a marker, because this prompt
    /// instructs the model to say where each reading sat against the member's own usual and that
    /// is one of the natural ways to answer. A review is written once, so a false discard costs
    /// the caregiver that day for good.
    /// </summary>
    [Theory]
    [InlineData("Her resting rate was 59 bpm, consistent with her usual 58.")]
    [InlineData("A quiet day, consistent with her usual Sundays.")]
    public void NamesACondition_DoesNotFireOnTheComparisonThePromptAsksFor(string text)
    {
        Assert.Null(DaybookPrompt.NamesACondition(text));
    }

    /// <summary>Proposing a treatment is the other half of the same line.</summary>
    [Theory]
    [InlineData("It may help to increase the dose in the evening.")]
    [InlineData("He should take something to help him sleep.")]
    public void NamesACondition_RefusesATreatmentProposal(string text)
    {
        Assert.NotNull(DaybookPrompt.NamesACondition(text));
    }

    // ── The gloss rule ───────────────────────────────────────────────────────

    /// <summary>
    /// A precise term earns its place by explaining itself where it is first used. This is the
    /// readability half of the allowance, and without it the register drifts back into the
    /// clinic-speak it rules out.
    /// </summary>
    [Fact]
    public void UnglossedTerm_FlagsAPreciseTermUsedBare()
    {
        var flagged = DaybookPrompt.UnglossedTerm("Her sleep efficiency was 78% last night.");

        Assert.Equal("sleep efficiency", flagged);
    }

    [Theory]
    [InlineData("Her sleep efficiency — how much of her time in bed she was actually asleep — was 78%.")]
    [InlineData("Her sleep efficiency (the share of time in bed she was actually asleep) was 78%.")]
    [InlineData("Her sleep efficiency, which measures how much of her time in bed she was asleep, was 78%.")]
    public void UnglossedTerm_AcceptsATermThatExplainsItself(string text)
    {
        Assert.Null(DaybookPrompt.UnglossedTerm(text));
    }

    /// <summary>
    /// A decimal point is not a sentence boundary. This text quotes figures by design, and
    /// splitting "95.4%" in half used to move a term and the gloss that follows its figure into
    /// different fragments — flagging a compliant review, which a caregiver loses for good since
    /// a review is written once.
    /// </summary>
    [Theory]
    [InlineData("Her SpO2 sat at 95.4% — the share of oxygen her blood was carrying.")]
    [InlineData("Her oxygen saturation averaged 95.4%, meaning the share of oxygen in her blood, and held steady.")]
    public void UnglossedTerm_DoesNotSplitASentenceAtADecimalPoint(string text)
    {
        Assert.Null(DaybookPrompt.UnglossedTerm(text));
    }

    /// <summary>An ordinary full stop still ends the sentence — the gloss must not be allowed to
    /// arrive one sentence late just because the split got laxer about decimals.</summary>
    [Fact]
    public void UnglossedTerm_StillSplitsOnAnOrdinaryFullStop()
    {
        var flagged = DaybookPrompt.UnglossedTerm(
            "Her sleep efficiency was 78%. That is how much of her time in bed she was asleep.");

        Assert.Equal("sleep efficiency", flagged);
    }

    /// <summary>
    /// Judged on first use only. Explaining the same term in every sentence is the padding this
    /// rule exists to prevent, so a term glossed once may afterwards be used plainly.
    /// </summary>
    [Fact]
    public void UnglossedTerm_JudgesFirstUseOnly()
    {
        var text = "Her sleep efficiency — how much of her time in bed she was asleep — was 78%. "
                   + "That sleep efficiency is close to her usual 71%.";

        Assert.Null(DaybookPrompt.UnglossedTerm(text));
    }

    /// <summary>
    /// The list is deliberately short. Requiring a gloss on terms that are precise but already
    /// plain would discard good reviews for explaining what needs no explaining.
    /// </summary>
    [Theory]
    [InlineData("His resting heart rate was 64 bpm.")]
    [InlineData("She spent 52 minutes in deep sleep.")]
    [InlineData("He recorded 4,200 steps and 18 active minutes.")]
    public void UnglossedTerm_DoesNotAskAPlainTermToExplainItself(string text)
    {
        Assert.Null(DaybookPrompt.UnglossedTerm(text));
    }

    // ── The echo guard ───────────────────────────────────────────────────────

    /// <summary>
    /// A reply that restates the brief is the model talking to itself, and the apps' own "no
    /// review yet" copy is a better thing to put in front of a caregiver.
    /// </summary>
    [Fact]
    public void ReadsLikeTheInstructions_CatchesTheBriefReadBack()
    {
        Assert.True(DaybookPrompt.ReadsLikeTheInstructions(
            "Past tense throughout: this day has finished and nothing in it is still accumulating."));
    }

    /// <summary>Matched against the reply with whitespace flattened, so a phrase the model wrapped
    /// across two lines is still the phrase it wrapped.</summary>
    [Fact]
    public void ReadsLikeTheInstructions_MatchesAcrossAWrappedLine()
    {
        Assert.True(DaybookPrompt.ReadsLikeTheInstructions(
            "Past tense\n   throughout: this day has finished."));
    }

    [Fact]
    public void ReadsLikeTheInstructions_LeavesAnOrdinaryReviewAlone()
    {
        Assert.False(DaybookPrompt.ReadsLikeTheInstructions(
            "Dad slept 6.2 hours, noticeably more than his usual 4.1."));
    }
}
