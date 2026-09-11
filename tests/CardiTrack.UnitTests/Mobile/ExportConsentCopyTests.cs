using CardiTrack.Application.Reports;
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
}
