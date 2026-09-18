using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Export;

namespace CardiTrack.UnitTests.Mobile;

public class JournalExportRequestsTests
{
    private static readonly Guid MemberId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Theory]
    [InlineData(JournalCadence.Daybook, DigestAudience.Daybook)]
    [InlineData(JournalCadence.Weekbook, DigestAudience.Weekbook)]
    [InlineData(JournalCadence.Monthbook, DigestAudience.Monthbook)]
    public void Audience_IsTheFinishedBook_NeverTheLiveGlance(
        JournalCadence cadence, DigestAudience expected)
    {
        Assert.Equal(expected, JournalExportRequests.Audience(cadence));
    }

    [Fact]
    public void Window_CapsAllTime_AtAYearInclusive()
    {
        var today = new DateOnly(2026, 9, 10);

        var (from, to) = JournalExportRequests.Window(today, windowDays: null);

        Assert.Equal(today, to);
        Assert.Equal(today.AddDays(-(JournalExportRequests.MaxRangeDays - 1)), from);
        Assert.Equal(JournalExportRequests.MaxRangeDays, to.DayNumber - from.DayNumber + 1);
    }

    [Fact]
    public void Window_HonoursAShorterChip()
    {
        var today = new DateOnly(2026, 9, 10);

        var (from, to) = JournalExportRequests.Window(today, windowDays: 7);

        Assert.Equal(today, to);
        Assert.Equal(new DateOnly(2026, 9, 4), from);
    }

    [Fact]
    public void ConsentAndGenerate_ShareTheSameJournalSnapshot()
    {
        var from = new DateOnly(2026, 2, 7);
        var to = new DateOnly(2026, 3, 9);
        var consent = JournalExportRequests.Consent(
            MemberId, from, to, ReportFormat.Pdf, DigestAudience.Weekbook,
            entryDate: null, ExportConsentMethod.Password);
        var generate = JournalExportRequests.Generate(
            MemberId, "Dad — Weekbooks", from, to, ReportFormat.Pdf,
            DigestAudience.Weekbook, entryDate: null, "token");

        Assert.True(consent.IncludeJournals);
        Assert.True(consent.IncludeTrends);
        Assert.False(consent.IncludeMetrics);
        Assert.False(consent.IncludeNotices);
        Assert.Equal(consent.IncludeTrends, generate.IncludeTrends);
        Assert.Equal(consent.JournalAudience, generate.JournalAudience);
        Assert.Equal(consent.JournalEntryDate, generate.JournalEntryDate);
        Assert.Equal(consent.DateRangeFrom, generate.DateRangeFrom);
        Assert.Equal(consent.DateRangeTo, generate.DateRangeTo);
        Assert.Equal("token", generate.ConsentToken);
        Assert.Null(consent.JournalEntryDate);
        Assert.Equal(DigestAudience.Weekbook, generate.JournalAudience);
    }

    [Fact]
    public void ConsentAndGenerate_ShareAFingerprint()
    {
        var from = new DateOnly(2026, 2, 7);
        var to = new DateOnly(2026, 3, 9);
        var generate = JournalExportRequests.Generate(
            MemberId, "Dad — Daybooks", from, to, ReportFormat.Pdf,
            DigestAudience.Daybook, entryDate: null, "token");
        var asConsent = JournalExportRequests.Consent(
            MemberId, from, to, ReportFormat.Pdf, DigestAudience.Daybook,
            entryDate: null, ExportConsentMethod.Biometric);
        var fromConsent = JournalExportRequests.Generate(
            MemberId, title: null, asConsent.DateRangeFrom, asConsent.DateRangeTo,
            asConsent.Format, asConsent.JournalAudience!.Value, asConsent.JournalEntryDate,
            "other-token");

        Assert.Equal(
            CardiTrack.Application.Reports.ExportConsentPolicy.Fingerprint(generate),
            CardiTrack.Application.Reports.ExportConsentPolicy.Fingerprint(fromConsent));
    }

    [Fact]
    public void OneEntry_PinsTheDayAndTheBook()
    {
        var day = new DateOnly(2026, 2, 10);
        var generate = JournalExportRequests.Generate(
            MemberId, "Dad — Daybook", day, day, ReportFormat.Csv,
            DigestAudience.Daybook, day, "token");

        Assert.Equal(day, generate.JournalEntryDate);
        Assert.Equal(DigestAudience.Daybook, generate.JournalAudience);
        Assert.Equal(day, generate.DateRangeFrom);
        Assert.Equal(day, generate.DateRangeTo);
        Assert.Equal(ReportFormat.Csv, generate.Format);
        Assert.False(generate.IncludeTrends);
    }

    [Fact]
    public void Pdf_AsksForTheTrendCharts_CsvDoesNot()
    {
        var from = new DateOnly(2026, 2, 7);
        var to = new DateOnly(2026, 3, 9);

        var pdf = JournalExportRequests.Generate(
            MemberId, "Dad — Daybooks", from, to, ReportFormat.Pdf,
            DigestAudience.Daybook, entryDate: null, "token");
        var csv = JournalExportRequests.Generate(
            MemberId, "Dad — Daybooks", from, to, ReportFormat.Csv,
            DigestAudience.Daybook, entryDate: null, "token");

        Assert.True(pdf.IncludeTrends);
        Assert.False(pdf.IncludeMetrics);
        Assert.False(csv.IncludeTrends);
    }

    [Fact]
    public void SnapToCadence_LeavesDaysAlone()
    {
        var from = new DateOnly(2026, 9, 9);
        var to = new DateOnly(2026, 9, 15);

        var snapped = JournalExportRequests.SnapToCadence(JournalCadence.Daybook, from, to);

        Assert.Equal((from, to), snapped);
    }

    [Fact]
    public void SnapToCadence_WidensWeeks_MondayToSunday()
    {
        // A Wednesday to the Friday of the week after.
        var from = new DateOnly(2026, 9, 9);
        var to = new DateOnly(2026, 9, 18);

        var (start, end) = JournalExportRequests.SnapToCadence(JournalCadence.Weekbook, from, to);

        Assert.Equal(new DateOnly(2026, 9, 7), start);
        Assert.Equal(new DateOnly(2026, 9, 20), end);
        Assert.Equal(DayOfWeek.Monday, start.DayOfWeek);
        Assert.Equal(DayOfWeek.Sunday, end.DayOfWeek);
    }

    [Fact]
    public void SnapToCadence_WidensMonths_FirstToLast()
    {
        var from = new DateOnly(2026, 1, 20);
        var to = new DateOnly(2026, 2, 3);

        var (start, end) = JournalExportRequests.SnapToCadence(JournalCadence.Monthbook, from, to);

        Assert.Equal(new DateOnly(2026, 1, 1), start);
        // February 2026 has 28 days; the snap must read the calendar, not assume 30.
        Assert.Equal(new DateOnly(2026, 2, 28), end);
    }

    [Fact]
    public void SnapToCadence_OrdersTheEnds_WhicheverWayTheyWerePicked()
    {
        var snapped = JournalExportRequests.SnapToCadence(
            JournalCadence.Daybook, new DateOnly(2026, 9, 15), new DateOnly(2026, 9, 9));

        Assert.Equal((new DateOnly(2026, 9, 9), new DateOnly(2026, 9, 15)), snapped);
    }

    [Fact]
    public void SnapToCadence_TrimsTheStart_WhenTheRangeOutrunsTheApiCeiling()
    {
        var from = new DateOnly(2024, 1, 1);
        var to = new DateOnly(2026, 9, 18);

        var (start, end) = JournalExportRequests.SnapToCadence(JournalCadence.Daybook, from, to);

        // The recent end is the one being asked about, so the oldest days are the ones dropped.
        Assert.Equal(to, end);
        Assert.Equal(JournalExportRequests.MaxRangeDays, end.DayNumber - start.DayNumber + 1);
    }

    [Theory]
    [InlineData(JournalCadence.Daybook, "2026-09-12", "2026-09-18", 7)]
    [InlineData(JournalCadence.Weekbook, "2026-08-17", "2026-09-20", 5)]
    [InlineData(JournalCadence.Monthbook, "2026-01-01", "2026-03-31", 3)]
    public void PeriodCount_CountsInTheCadencesOwnUnit(
        JournalCadence cadence, string from, string to, int expected)
    {
        var count = JournalExportRequests.PeriodCount(
            cadence, DateOnly.Parse(from), DateOnly.Parse(to));

        Assert.Equal(expected, count);
    }
}
