using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Reports;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Export;

namespace CardiTrack.UnitTests.Mobile;

public class ExportConsentCopyTests
{
    [Fact]
    public void ReuseNotice_FormatsLocalDates_AndNamesTheReuse()
    {
        var recorded = new DateTimeOffset(2026, 9, 11, 15, 0, 0, TimeSpan.Zero);
        var until = new DateTimeOffset(2026, 9, 25, 15, 0, 0, TimeSpan.Zero);

        var notice = ExportConsentCopy.ReuseNotice(recorded, until);

        Assert.Equal(
            ExportConsentPolicy.ReuseNotice(
                recorded.ToLocalTime().ToString("d MMM yyyy"),
                until.ToLocalTime().ToString("d MMM yyyy")),
            notice);
        Assert.Contains("We're using the confirmation you gave on", notice);
        Assert.Contains("You can stop this in Settings", notice);
    }

    [Fact]
    public void HistorySummary_FormatsRememberUntilInLocalTime()
    {
        var until = new DateTimeOffset(2026, 9, 18, 15, 0, 0, TimeSpan.Zero);
        var item = new ExportConsentHistoryItem
        {
            Id = Guid.NewGuid(),
            RecordedAt = new DateTimeOffset(2026, 9, 11, 15, 0, 0, TimeSpan.Zero),
            Method = ExportConsentMethod.Biometric,
            RememberFor = ExportConsentRememberFor.OneWeek,
            RememberUntil = until,
            CanRevoke = true,
            CanReuse = true,
            Reused = false,
            Summary = "server UTC line — the client must not show this"
        };

        var summary = ExportConsentCopy.HistorySummary(item);

        Assert.Contains(until.ToLocalTime().ToString("d MMM yyyy"), summary);
        Assert.Contains("In force until", summary);
        Assert.Contains("fingerprint or face unlock", summary);
        Assert.DoesNotContain("server UTC line", summary);
    }
}
