using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins the summary-refresh waivers to their boundaries: a yellow window or an SSA jump since
/// the last summary, a daily reading that would fire an R1 statistical rule, or a jump from
/// yesterday. These are the cases the 20-minute floor must not sit in front of.
/// </summary>
public class DigestRefreshRulesTests
{
    private static readonly DateTime PreviousSummaryAt = new(2026, 8, 10, 5, 20, 0, DateTimeKind.Utc);

    private static PatternBaseline Baseline() => new()
    {
        CardiMemberId = Guid.NewGuid(),
        PeriodDays = 30,
        AvgSteps = 6000,
        AvgRestingHeartRate = 62,
        StdDevHeartRate = 2.0m,
        AvgSleepMinutes = 420,
    };

    private static ActivityLog Log(
        DateOnly date, int? steps = null, int? restingHr = null, int? sleepMinutes = null,
        int? avgHr = null, decimal? spo2 = null) => new()
    {
        Date = date,
        Steps = steps,
        RestingHeartRate = restingHr,
        SleepMinutes = sleepMinutes,
        AvgHeartRate = avgHr,
        SpO2Average = spo2,
    };

    private static RealtimeAssessment Assessment(
        AlertSeverity? severity, double score, DateTime generatedAt) => new()
    {
        Severity = severity,
        HrDeviationScore = score,
        GeneratedAtUtc = generatedAt,
    };

    // ── samples indicate a problem ────────────────────────────────────────────

    [Fact]
    public void SamplesIndicateAProblem_WhenAYellowWindowLandedAfterTheSummary()
    {
        Assert.True(DigestRefreshRules.SamplesIndicateAProblem(
            Assessment(AlertSeverity.Yellow, score: 1.0, PreviousSummaryAt.AddMinutes(2)),
            PreviousSummaryAt));
    }

    [Fact]
    public void SamplesIndicateAProblem_WhenTheSsaScoreIsAJump_EvenIfTheModelSaidGreen()
    {
        Assert.True(DigestRefreshRules.SamplesIndicateAProblem(
            Assessment(AlertSeverity.Green, DigestRefreshRules.SampleJumpScore, PreviousSummaryAt.AddMinutes(1)),
            PreviousSummaryAt));
    }

    [Fact]
    public void SamplesIndicateAProblem_StaysQuiet_ForAnOrdinaryGreenWindow()
    {
        Assert.False(DigestRefreshRules.SamplesIndicateAProblem(
            Assessment(AlertSeverity.Green, score: 1.2, PreviousSummaryAt.AddMinutes(1)),
            PreviousSummaryAt));
    }

    [Fact]
    public void SamplesIndicateAProblem_StaysQuiet_WhenTheWindowPredatesTheSummary()
    {
        Assert.False(DigestRefreshRules.SamplesIndicateAProblem(
            Assessment(AlertSeverity.Red, score: 8.0, PreviousSummaryAt.AddMinutes(-1)),
            PreviousSummaryAt));
    }

    [Fact]
    public void SamplesIndicateAProblem_StaysQuiet_WhenThereIsNoAssessment()
    {
        Assert.False(DigestRefreshRules.SamplesIndicateAProblem(null, PreviousSummaryAt));
    }

    [Fact]
    public void SamplesIndicateAProblem_TheJumpScoreBoundary_IsInclusive()
    {
        Assert.True(DigestRefreshRules.SamplesIndicateAProblem(
            Assessment(AlertSeverity.Green, DigestRefreshRules.SampleJumpScore, PreviousSummaryAt.AddSeconds(1)),
            PreviousSummaryAt));
        Assert.False(DigestRefreshRules.SamplesIndicateAProblem(
            Assessment(AlertSeverity.Green, DigestRefreshRules.SampleJumpScore - 0.01, PreviousSummaryAt.AddSeconds(1)),
            PreviousSummaryAt));
    }

    // ── differ from the baseline ──────────────────────────────────────────────

    [Fact]
    public void ReadingsDivergeFromBaseline_WhenLastNightWasIrregular()
    {
        var today = Log(new DateOnly(2026, 8, 10), sleepMinutes: 250);

        Assert.True(DigestRefreshRules.ReadingsDivergeFromBaseline(Baseline(), today, yesterday: null));
    }

    [Fact]
    public void ReadingsDivergeFromBaseline_WhenTodaysRestingHrIsElevated()
    {
        // Same yardstick as ElevatedHeartRate: average 62 + max(4, 5) = 67. 80 is well above.
        var today = Log(new DateOnly(2026, 8, 10), restingHr: 80);

        Assert.True(DigestRefreshRules.ReadingsDivergeFromBaseline(Baseline(), today, yesterday: null));
    }

    [Fact]
    public void ReadingsDivergeFromBaseline_WhenYesterdaysStepsDeclined()
    {
        var yesterday = Log(new DateOnly(2026, 8, 9), steps: 3000);

        Assert.True(DigestRefreshRules.ReadingsDivergeFromBaseline(Baseline(), today: null, yesterday));
    }

    [Fact]
    public void ReadingsDivergeFromBaseline_DoesNotTreatTodaysPartialStepsAsADecline()
    {
        // A morning's 2,000 steps against a 6,000 usual would fire ActivityDecline if we passed
        // today as "yesterday". The rule is yesterday-only so every member does not waive the
        // floor at breakfast.
        var today = Log(new DateOnly(2026, 8, 10), steps: 2000);
        var yesterday = Log(new DateOnly(2026, 8, 9), steps: 6100);

        Assert.False(DigestRefreshRules.ReadingsDivergeFromBaseline(Baseline(), today, yesterday));
    }

    /// <summary>
    /// The two whole-day rules the waiver missed: a heart that worked on a quiet day, and a long
    /// unbroken stretch in the chair. Both fire alerts, so a summary written before the alert
    /// lands must not be the one the caregiver still has in front of them afterwards.
    /// </summary>
    [Fact]
    public void ReadingsDivergeFromBaseline_WhenYesterdaysHeartWorkedOnAQuietDay()
    {
        var yesterday = Log(new DateOnly(2026, 8, 9), steps: 3000);
        yesterday.ModerateZoneMinutes = 40;

        Assert.True(DigestRefreshRules.ReadingsDivergeFromBaseline(Baseline(), today: null, yesterday));
    }

    [Fact]
    public void ReadingsDivergeFromBaseline_WhenYesterdayHeldALongStillStretch()
    {
        var yesterday = Log(new DateOnly(2026, 8, 9), steps: 6100);
        yesterday.LongestSedentaryStretchMinutes = 300;

        Assert.True(DigestRefreshRules.ReadingsDivergeFromBaseline(Baseline(), today: null, yesterday));
    }

    /// <summary>
    /// Both read a figure that accumulates over the day, so today's is a morning's worth — the
    /// same trap steps-decline is kept off today's row for.
    /// </summary>
    [Fact]
    public void ReadingsDivergeFromBaseline_DoesNotReadTodaysPartialDayAsEitherOfThem()
    {
        var today = Log(new DateOnly(2026, 8, 10), steps: 2000);
        today.ModerateZoneMinutes = 40;
        today.LongestSedentaryStretchMinutes = 300;
        var yesterday = Log(new DateOnly(2026, 8, 9), steps: 6100);

        Assert.False(DigestRefreshRules.ReadingsDivergeFromBaseline(Baseline(), today, yesterday));
    }

    [Fact]
    public void ReadingsDivergeFromBaseline_StaysQuiet_WithoutABaseline()
    {
        var today = Log(new DateOnly(2026, 8, 10), restingHr: 110, sleepMinutes: 180);

        Assert.False(DigestRefreshRules.ReadingsDivergeFromBaseline(null, today, yesterday: null));
    }

    // ── huge jump from the previous ───────────────────────────────────────────

    [Fact]
    public void ReadingsJumpedFromPrevious_WhenRestingHrJumpedThirtyPercent()
    {
        var yesterday = Log(new DateOnly(2026, 8, 9), restingHr: 60);
        var today = Log(new DateOnly(2026, 8, 10), restingHr: 80);

        Assert.True(DigestRefreshRules.ReadingsJumpedFromPrevious(today, yesterday));
    }

    [Fact]
    public void ReadingsJumpedFromPrevious_WhenSleepJumpedThirtyPercent()
    {
        var yesterday = Log(new DateOnly(2026, 8, 9), sleepMinutes: 420);
        var today = Log(new DateOnly(2026, 8, 10), sleepMinutes: 280);

        Assert.True(DigestRefreshRules.ReadingsJumpedFromPrevious(today, yesterday));
    }

    [Fact]
    public void ReadingsJumpedFromPrevious_WhenSpO2DroppedThreePoints()
    {
        var yesterday = Log(new DateOnly(2026, 8, 9), spo2: 97m);
        var today = Log(new DateOnly(2026, 8, 10), spo2: 94m);

        Assert.True(DigestRefreshRules.ReadingsJumpedFromPrevious(today, yesterday));
    }

    [Fact]
    public void ReadingsJumpedFromPrevious_IgnoresTodaysPartialSteps()
    {
        var yesterday = Log(new DateOnly(2026, 8, 9), steps: 8000);
        var today = Log(new DateOnly(2026, 8, 10), steps: 400);

        Assert.False(DigestRefreshRules.ReadingsJumpedFromPrevious(today, yesterday));
    }

    [Fact]
    public void ReadingsJumpedFromPrevious_StaysQuiet_InsideTheBand()
    {
        var yesterday = Log(new DateOnly(2026, 8, 9), restingHr: 62, sleepMinutes: 420, spo2: 96m);
        var today = Log(new DateOnly(2026, 8, 10), restingHr: 64, sleepMinutes: 430, spo2: 95m);

        Assert.False(DigestRefreshRules.ReadingsJumpedFromPrevious(today, yesterday));
    }

    [Fact]
    public void ReadingsJumpedFromPrevious_NeedsBothDays()
    {
        var today = Log(new DateOnly(2026, 8, 10), restingHr: 110);

        Assert.False(DigestRefreshRules.ReadingsJumpedFromPrevious(today, yesterday: null));
        Assert.False(DigestRefreshRules.ReadingsJumpedFromPrevious(today: null, yesterday: today));
    }
}
