using CardiTrack.Application.Reports;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Charts;

namespace CardiTrack.UnitTests.Services;

public class ReportJournalScopeTests
{
    [Fact]
    public void ChartWindow_KeepsThePickedRange_WhenNoEntryIsPinned()
    {
        var from = new DateOnly(2026, 2, 1);
        var to = new DateOnly(2026, 2, 28);

        var window = ReportJournalScope.ChartWindow(from, to, journalEntryDate: null, audience: null);

        Assert.Equal(from, window.From);
        Assert.Equal(to, window.To);
    }

    [Fact]
    public void ChartWindow_IsTheFortnightEndingOnADaybookEntry()
    {
        var day = new DateOnly(2026, 2, 20);

        var window = ReportJournalScope.ChartWindow(
            day, day, day, DigestAudience.Daybook);

        Assert.Equal(day.AddDays(-(ReportJournalScope.DayAndWeekChartDays - 1)), window.From);
        Assert.Equal(day, window.To);
        Assert.Equal(ReportJournalScope.DayAndWeekChartDays, window.To.DayNumber - window.From.DayNumber + 1);
    }

    [Fact]
    public void ChartWindow_IsThirtyDaysEndingOnAMonthbookEntry()
    {
        var day = new DateOnly(2026, 2, 28);

        var window = ReportJournalScope.ChartWindow(
            day, day, day, DigestAudience.Monthbook);

        Assert.Equal(day.AddDays(-(ReportJournalScope.MonthChartDays - 1)), window.From);
        Assert.Equal(day, window.To);
        Assert.Equal(ReportJournalScope.MonthChartDays, window.To.DayNumber - window.From.DayNumber + 1);
    }

    [Fact]
    public void ChartWindows_StayInStepWithTheJournalPage()
    {
        Assert.Equal(TrendAwareness.WindowDays, ReportJournalScope.DayAndWeekChartDays);
        Assert.Equal(TrendAwareness.MonthWindowDays, ReportJournalScope.MonthChartDays);
    }
}
