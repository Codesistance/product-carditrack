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
        Assert.False(consent.IncludeMetrics);
        Assert.False(consent.IncludeNotices);
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
    }
}
