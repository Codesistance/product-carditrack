using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// What a window with a hole in it says to the model, and what the model is forbidden to say back.
/// <para>
/// Both surfaces got this wrong on the same data. Asked "why aren't there steps tracked for
/// Monday?" member chat answered with a different day's step count (#531); the Activity chart
/// labelled the same four days "No data recorded" (#532). The readings arrived days later — the
/// highest step count of the fortnight was inside them — so each surface had stated as a settled
/// fact about a person something that was only ever a fact about the data.
/// </para>
/// </summary>
public class DataGapPromptTests
{
    private static readonly DateOnly Today = new(2026, 9, 13);

    private static ActivityLog Log(DateOnly date, int steps) => new() { Date = date, Steps = steps };

    /// <summary>
    /// The dates, spelled out. <c>DailyLines</c> omits a day it holds no row for, and an absence
    /// inferred from a row that is simply not there is an inference the model does not make.
    /// </summary>
    [Fact]
    public void MissingDaysLine_NamesEveryDayNoReadingArrivedFor()
    {
        var line = MedicalPromptBlocks.MissingDaysLine(
            [Log(new DateOnly(2026, 9, 3), 5200), Log(new DateOnly(2026, 9, 8), 6100), Log(Today, 900)],
            (new DateOnly(2026, 9, 3), Today),
            Today);

        Assert.NotNull(line);
        Assert.Contains("Sep 4", line);
        Assert.Contains("Sep 5", line);
        Assert.Contains("Sep 6", line);
        Assert.Contains("Sep 7", line);
        Assert.DoesNotContain("Sep 3", line);
        Assert.DoesNotContain("Sep 8", line);
    }

    /// <summary>
    /// The half that carries the meaning. A list of dates alone reads as a list of days the person
    /// was still — which is the reading that caused the issue in the first place.
    /// </summary>
    [Fact]
    public void MissingDaysLine_SaysAbsenceIsAboutArrivalNotAboutThePerson()
    {
        var line = MedicalPromptBlocks.MissingDaysLine(
            [Log(new DateOnly(2026, 9, 11), 4400), Log(Today, 1200)],
            (new DateOnly(2026, 9, 11), Today),
            Today);

        Assert.NotNull(line);
        Assert.Contains("reached us", line);
        Assert.Contains("not that the person recorded nothing", line);
    }

    /// <summary>
    /// Today is already carried by <c>DailyLines</c>' synthesised row, which writes "not measured"
    /// in every column. Naming it here too would state the same absence twice in two vocabularies.
    /// </summary>
    [Fact]
    public void MissingDaysLine_LeavesTodayToTheAnchorRow()
    {
        var line = MedicalPromptBlocks.MissingDaysLine(
            [Log(Today.AddDays(-2), 7300), Log(Today.AddDays(-1), 6800)],
            (Today.AddDays(-2), Today),
            Today);

        Assert.Null(line);
    }

    [Fact]
    public void MissingDaysLine_IsSilentOnACompleteWindow()
    {
        var line = MedicalPromptBlocks.MissingDaysLine(
            [Log(Today.AddDays(-1), 6800), Log(Today, 2100)],
            (Today.AddDays(-1), Today),
            Today);

        Assert.Null(line);
    }

    /// <summary>
    /// A member with no readings at all is a different statement, and "No recent activity data."
    /// already makes it better than a list of every date in the window would.
    /// </summary>
    [Fact]
    public void MissingDaysLine_IsSilentWhenThereAreNoReadingsAtAll()
    {
        var line = MedicalPromptBlocks.MissingDaysLine(
            [], (Today.AddDays(-6), Today), Today);

        Assert.Null(line);
    }

    /// <summary>
    /// Every clinical read that is handed daily readings carries the rule — not just the default
    /// rung. The question that failed was a "why", which lands on investigation or inference, and
    /// those two write their own briefs rather than sharing the analysis one.
    /// </summary>
    /// <remarks>
    /// The set is derived from the catalogue rather than listed here, so a rung added later with
    /// <see cref="DataQueryKind.RecentActivity"/> in its datasets cannot reach a caregiver without
    /// the rule. The steer rungs are excluded by the same derivation: they are handed no readings,
    /// so a rule about missing days would be a rule about something they never see.
    /// </remarks>
    [Fact]
    public void EveryClinicalBriefCarriesTheGapRule()
    {
        var readingRungs = ChatWorkflowCatalogue.All
            .Where(w => w.AllowedDatasets.Contains(DataQueryKind.RecentActivity))
            .Select(w => w.Id)
            .Where(MemberChatService.HandlerBriefs.ContainsKey)
            .ToList();

        Assert.Contains(MemberChatWorkflow.Investigation, readingRungs);
        Assert.Contains(MemberChatWorkflow.Inference, readingRungs);

        foreach (var workflow in readingRungs)
        {
            var brief = MemberChatService.HandlerBriefs[workflow];
            Assert.True(
                brief.Contains("no reading arrived for it", StringComparison.Ordinal),
                $"{workflow} does not tell the model what a missing day means.");
            Assert.True(
                brief.Contains("never say why it is missing", StringComparison.Ordinal),
                $"{workflow} does not forbid guessing why a day is missing.");
        }
    }

    /// <summary>
    /// The specific wrong answer from the screenshot on #531: a different day's figure offered in
    /// place of the day that was asked about.
    /// </summary>
    [Fact]
    public void TheGapRuleForbidsSubstitutingAnotherDaysFigure()
    {
        Assert.Contains("Never answer with a different day's figure", MedicalPromptBlocks.DataGapRule);
    }
}
