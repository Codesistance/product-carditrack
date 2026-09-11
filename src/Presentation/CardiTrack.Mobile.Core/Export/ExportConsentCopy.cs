using CardiTrack.Application.Reports;

namespace CardiTrack.Mobile.Core.Export;

/// <summary>Local-date wording for a reused standing export confirmation.</summary>
public static class ExportConsentCopy
{
    public static string ReuseNotice(DateTimeOffset recordedAt, DateTimeOffset rememberUntil)
    {
        var recordedOn = recordedAt.ToLocalTime().ToString("d MMM yyyy");
        var until = rememberUntil.ToLocalTime().ToString("d MMM yyyy");
        return ExportConsentPolicy.ReuseNotice(recordedOn, until);
    }
}
