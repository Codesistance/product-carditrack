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

    [Theory]
    [InlineData(ExportConsentRememberFor.ThisExport, null)]
    [InlineData(ExportConsentRememberFor.OneWeek, 7)]
    [InlineData(ExportConsentRememberFor.TwoWeeks, 14)]
    [InlineData(ExportConsentRememberFor.OneMonth, 30)]
    public void RememberDuration_CapsAtAMonth(ExportConsentRememberFor rememberFor, int? days)
    {
        var duration = ExportConsentPolicy.RememberDuration(rememberFor);
        if (days is null)
            Assert.Null(duration);
        else
            Assert.Equal(TimeSpan.FromDays(days.Value), duration);
    }

    [Fact]
    public void ReuseNotice_AlwaysNamesTheReuse_AndPointsAtSettings()
    {
        var notice = ExportConsentPolicy.ReuseNotice("11 Sep 2026", "25 Sep 2026");

        Assert.Contains("We're using the confirmation you gave on 11 Sep 2026", notice);
        Assert.Contains("It stays in force until 25 Sep 2026", notice);
        Assert.Contains("You can stop this in Settings", notice);
    }
}
