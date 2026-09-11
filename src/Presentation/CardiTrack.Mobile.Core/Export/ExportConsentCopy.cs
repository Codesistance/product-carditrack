using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Reports;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Mobile.Core.Export;

/// <summary>Local-date wording for export confirmation history and reuse.</summary>
public static class ExportConsentCopy
{
    public static string ReuseNotice(DateTimeOffset recordedAt, DateTimeOffset rememberUntil)
    {
        var recordedOn = Day(recordedAt);
        var until = Day(rememberUntil);
        return ExportConsentPolicy.ReuseNotice(recordedOn, until);
    }

    /// <summary>
    /// Settings line. Dates are the device's local day so "In force until"
    /// matches the caption's "Given …" rather than UTC. A standing grant whose
    /// wording has changed is still stoppable, but is not described as in force.
    /// </summary>
    public static string HistorySummary(ExportConsentHistoryItem item)
    {
        var proof = item.Method == ExportConsentMethod.Biometric
            ? "fingerprint or face unlock"
            : "password";

        if (item.Reused)
            return $"Reused an earlier confirmation · {proof}";

        if (item.RevokedAt is { } revoked)
            return $"Stopped on {Day(revoked)} · {proof}";

        if (item.CanRevoke && item.RememberUntil is { } until)
        {
            return item.CanReuse
                ? $"In force until {Day(until)} · {proof}"
                : $"Needs a new confirmation · kept until {Day(until)} · {proof}";
        }

        if (item.RememberUntil is { } remembered)
            return $"Kept until {Day(remembered)} · {proof}";

        return $"Just this export · {proof}";
    }

    private static string Day(DateTimeOffset value) =>
        value.ToLocalTime().ToString("d MMM yyyy");
}
