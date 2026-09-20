using CardiTrack.Application.Reports;
using CardiTrack.Domain.Entities;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The comparison every export format shares. It is computed rather than narrated precisely so a
/// failed model call cannot take it with it, so what these pin is the arithmetic and the honesty
/// of the gaps — a metric with no learned usual, a member with no baseline at all.
/// </summary>
public class ReportComparisonTests
{
    private static readonly DateOnly From = new(2026, 9, 7);
    private static readonly DateOnly To = new(2026, 9, 20);
    private readonly Guid _memberId = Guid.NewGuid();

    [Fact]
    public void AMemberWithNoBaselineGetsNoComparisonAtAll()
    {
        // Rather than a table of dashes. An export for someone still being learned should not
        // imply a comparison was attempted and came back empty.
        var member = Member(baseline: null, steps: 4000);

        Assert.Empty(ReportComparison.For(member, ageYears: 78));
    }

    [Fact]
    public void ThePeriodAverageIsMeasuredAgainstTheirOwnUsual()
    {
        var member = Member(Baseline(avgSteps: 5000), steps: 4000);

        var steps = ReportComparison.For(member, 78).Single(r => r.Metric == "Steps");

        Assert.Equal(4000, steps.PeriodAverage);
        Assert.Equal(5000, steps.Usual);
        Assert.Equal(-20, steps.ChangePercent);
        Assert.Equal(14, steps.MeasuredDays);
    }

    [Fact]
    public void SleepIsReportedInHours_NotMinutes()
    {
        // A report is read by people, and 432 is not a night's sleep to anyone but a database.
        var member = Member(Baseline(avgSleepMinutes: 450), sleepMinutes: 432);

        var sleep = ReportComparison.For(member, 78).Single(r => r.Metric == "Sleep");

        Assert.Equal("hours a night", sleep.Unit);
        Assert.Equal(7.2m, sleep.PeriodAverage);
        Assert.Equal(7.5m, sleep.Usual);
    }

    [Fact]
    public void ThePublishedBandNamesWhoPublishedIt()
    {
        // An unattributed range in a document a caregiver may hand to a clinician reads as ours.
        var member = Member(Baseline(avgRestingHeartRate: 62), restingHr: 71);

        var heart = ReportComparison.For(member, 78).Single(r => r.Metric == "Resting heart rate");

        Assert.Equal(60, heart.BandLow);
        Assert.Equal(100, heart.BandHigh);
        Assert.Equal("AHA", heart.BandSource);
    }

    [Fact]
    public void AMetricWithNoLearnedUsualStillReportsWhatWasMeasured()
    {
        // Blood oxygen has no baseline anywhere in the product. The period average and the
        // published band are both real; the "their usual" column is honestly empty.
        var member = Member(Baseline(avgSteps: 5000), spO2: 95);

        var oxygen = ReportComparison.For(member, 78).Single(r => r.Metric == "Blood oxygen");

        Assert.Equal(95, oxygen.PeriodAverage);
        Assert.Null(oxygen.Usual);
        Assert.Null(oxygen.ChangePercent);
        Assert.Equal("WHO", oxygen.BandSource);
    }

    [Fact]
    public void AMetricNothingMeasuredIsLeftOutEntirely()
    {
        var member = Member(Baseline(avgSteps: 5000), steps: 4000);

        Assert.DoesNotContain(
            ReportComparison.For(member, 78),
            row => row.Metric == "Overnight heart rate variability");
    }

    [Fact]
    public void TheRenderedBlockTellsTheModelNotToWorkOneOut()
    {
        var rows = ReportComparison.For(Member(Baseline(avgSteps: 5000), steps: 4000), 78);

        var rendered = ReportComparison.Render(rows);

        Assert.Contains("already computed", rendered);
        Assert.Contains("never work out a comparison yourself", rendered);
        Assert.Contains("their own usual is 5000, so 20% below it", rendered);
    }

    /// <summary>
    /// A member wearing two watches has two rows a day, and an export is a per-day document: the
    /// average must be over days, and the measured-day count must be a count of days.
    /// </summary>
    [Fact]
    public void ASecondDeviceRowForTheSameDayNeitherSkewsTheAverageNorInflatesTheDayCount()
    {
        var member = Member(Baseline(avgSteps: 5_000), steps: 4_000);
        var dayCount = member.ActivityLogs.Count;

        var older = member.ActivityLogs
            .Select(log => new ActivityLog
            {
                CardiMemberId = _memberId,
                Date = log.Date,
                Steps = 10_000,
                CreatedDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            });
        var newer = member.ActivityLogs
            .Select(log => new ActivityLog
            {
                CardiMemberId = _memberId,
                Date = log.Date,
                Steps = log.Steps,
                CreatedDate = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc),
            });

        var twoDevices = new ReportMemberData(
            member.Member, older.Concat(newer).ToList(), [], [], [], [])
        {
            Baseline = member.Baseline,
        };

        var row = ReportComparison.For(twoDevices, ageYears: 78).Single(r => r.Metric == "Steps");

        Assert.Equal(dayCount, row.MeasuredDays);
        Assert.Equal(4_000m, row.PeriodAverage);
    }

    /// <summary>
    /// The band beside a row has to describe the row's own measurement. The adult respiratory rate
    /// this codebase holds is WHO's figure at rest, and this row is measured across a night's
    /// sleep — a different measurement, printed in a PDF a caregiver may hand to a clinician, who
    /// is exactly the reader who would take a published range at its word.
    /// </summary>
    [Fact]
    public void BreathingAsleepIsComparedAgainstTheirOwnUsualAndNoPublishedBand()
    {
        var member = Member(Baseline(avgOvernightBreathingRate: 15m), overnightBreathing: 17m);

        var row = ReportComparison.For(member, 78).Single(r => r.Metric == "Breathing rate asleep");

        // Their own usual still carries the row — dropping the band is not dropping the comparison.
        Assert.Equal(17m, row.PeriodAverage);
        Assert.Equal(15m, row.Usual);
        Assert.Equal(13m, row.ChangePercent);

        Assert.Null(row.BandLow);
        Assert.Null(row.BandHigh);
        Assert.Null(row.BandSource);

        // And the prompt the narrative is written from says nothing about a published range for it
        // either, since that is the half a caregiver never sees.
        var rendered = ReportComparison.Render(ReportComparison.For(member, 78));
        var line = rendered.Split(Environment.NewLine).Single(l => l.Contains("Breathing rate asleep"));
        Assert.DoesNotContain("published range", line);
    }

    [Fact]
    public void TheWakingBandStillCarriesTheRowsItDescribes()
    {
        // The counterpart: dropping the overnight band must not quietly drop the ones that are
        // measured the way their publisher measured them.
        var rows = ReportComparison.For(Member(Baseline(avgRestingHeartRate: 70), restingHr: 74, spO2: 96m), 78);

        var heart = rows.Single(r => r.Metric == "Resting heart rate");
        Assert.Equal(60m, heart.BandLow);
        Assert.Equal(100m, heart.BandHigh);
        Assert.Equal("AHA", heart.BandSource);

        var oxygen = rows.Single(r => r.Metric == "Blood oxygen");
        Assert.Equal("WHO", oxygen.BandSource);
    }

    private ReportMemberData Member(
        PatternBaseline? baseline,
        int? steps = null,
        int? restingHr = null,
        int? sleepMinutes = null,
        decimal? spO2 = null,
        decimal? overnightBreathing = null)
    {
        var logs = Enumerable.Range(0, To.DayNumber - From.DayNumber + 1)
            .Select(offset => new ActivityLog
            {
                CardiMemberId = _memberId,
                Date = From.AddDays(offset),
                Steps = steps,
                RestingHeartRate = restingHr,
                SleepMinutes = sleepMinutes,
                SpO2Average = spO2,
                OvernightBreathingRate = overnightBreathing,
            })
            .ToList();

        return new ReportMemberData(
            new CardiMember { Id = _memberId, Name = "Test Member", DateOfBirth = new DateOnly(1948, 3, 15) },
            logs, [], [], [], [])
        {
            Baseline = baseline,
        };
    }

    private PatternBaseline Baseline(
        int? avgSteps = null,
        int? avgRestingHeartRate = null,
        int? avgSleepMinutes = null,
        decimal? avgOvernightBreathingRate = null) => new()
    {
        CardiMemberId = _memberId,
        PeriodDays = 30,
        AvgSteps = avgSteps,
        AvgRestingHeartRate = avgRestingHeartRate,
        AvgSleepMinutes = avgSleepMinutes,
        AvgOvernightBreathingRate = avgOvernightBreathingRate,
    };
}
