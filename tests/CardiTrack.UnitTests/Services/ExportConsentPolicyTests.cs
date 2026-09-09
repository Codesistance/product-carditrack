using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.Reports;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Services;

public class ExportConsentPolicyTests
{
    private static GenerateReportRequest Request(
        Guid? memberA = null,
        Guid? memberB = null,
        bool includeJournals = false,
        DateOnly? journalDay = null) => new()
    {
        CardiMemberIds = memberB is { } b
            ? [memberA ?? Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), b]
            : [memberA ?? Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")],
        DateRangeFrom = new DateOnly(2026, 2, 7),
        DateRangeTo = new DateOnly(2026, 3, 9),
        Format = ReportFormat.Pdf,
        IncludeJournals = includeJournals,
        JournalEntryDate = journalDay
    };

    [Fact]
    public void Fingerprint_IsStable_ForTheSameSnapshot()
    {
        Assert.Equal(ExportConsentPolicy.Fingerprint(Request()), ExportConsentPolicy.Fingerprint(Request()));
    }

    [Fact]
    public void Fingerprint_IgnoresMemberOrder()
    {
        var a = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var b = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        Assert.Equal(
            ExportConsentPolicy.Fingerprint(Request(memberA: a, memberB: b)),
            ExportConsentPolicy.Fingerprint(new GenerateReportRequest
            {
                CardiMemberIds = [b, a],
                DateRangeFrom = new DateOnly(2026, 2, 7),
                DateRangeTo = new DateOnly(2026, 3, 9),
                Format = ReportFormat.Pdf
            }));
    }

    [Fact]
    public void Fingerprint_ChangesWhenASectionFlagChanges()
    {
        Assert.NotEqual(
            ExportConsentPolicy.Fingerprint(Request()),
            ExportConsentPolicy.Fingerprint(Request(includeJournals: true)));
    }

    [Fact]
    public void Fingerprint_ChangesWhenThePinnedJournalDayChanges()
    {
        Assert.NotEqual(
            ExportConsentPolicy.Fingerprint(Request(journalDay: new DateOnly(2026, 2, 10))),
            ExportConsentPolicy.Fingerprint(Request(journalDay: new DateOnly(2026, 2, 11))));
    }

    [Fact]
    public void Sha256_IsTheHashOfTheShownText()
    {
        Assert.Equal(
            ExportConsentPolicy.Sha256HexOf(ExportConsentPolicy.Text),
            ExportConsentPolicy.Sha256Hex);
        Assert.Equal(64, ExportConsentPolicy.Sha256Hex.Length);
    }
}
