using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Reports;

/// <summary>
/// The exact responsibility text shown before every export, and the fingerprint
/// that binds a recorded consent to one generate request.
/// </summary>
/// <remarks>
/// Changing <see cref="Text"/> is a new policy version: bump <see cref="Version"/>
/// in the same change so an investigator can tell which wording was accepted.
/// </remarks>
public static class ExportConsentPolicy
{
    public const string Version = "export-responsibility-2026-09";

    public const string Title = "You are responsible for this copy";

    public const string Text =
        "This file is a named health record. Once it leaves CardiTrack — saved, "
        + "shared, printed or forwarded — you are responsible for who sees it. "
        + "Only export if you are authorised to hold this person's health data, "
        + "and only share it with people who need it for their care.";

    public const string ConfirmPrompt = "I accept responsibility for this copy";

    /// <summary>How long a minted token may sit unused before generate must mint a new one.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long a standing grant may authorize later exports. Null means this
    /// export only — the two-minute <see cref="Lifetime"/> token.
    /// </summary>
    public static TimeSpan? RememberDuration(ExportConsentRememberFor rememberFor) => rememberFor switch
    {
        ExportConsentRememberFor.OneWeek => TimeSpan.FromDays(7),
        ExportConsentRememberFor.TwoWeeks => TimeSpan.FromDays(14),
        ExportConsentRememberFor.OneMonth => TimeSpan.FromDays(30),
        _ => null
    };

    /// <summary>Chooser labels, in the order the consent prompt offers them.</summary>
    public static readonly ExportConsentRememberFor[] RememberChoices =
    [
        ExportConsentRememberFor.ThisExport,
        ExportConsentRememberFor.OneWeek,
        ExportConsentRememberFor.TwoWeeks,
        ExportConsentRememberFor.OneMonth
    ];

    public static string RememberChoiceLabel(ExportConsentRememberFor rememberFor) => rememberFor switch
    {
        ExportConsentRememberFor.OneWeek => "1 week",
        ExportConsentRememberFor.TwoWeeks => "2 weeks",
        ExportConsentRememberFor.OneMonth => "1 month",
        _ => "Just this export"
    };

    /// <summary>
    /// Caregiver-facing line when a later export uses a standing grant.
    /// Dates are already local to whoever formatted <paramref name="recordedOn"/>
    /// and <paramref name="rememberUntil"/>.
    /// </summary>
    public static string ReuseNotice(string recordedOn, string rememberUntil) =>
        $"We're using the confirmation you gave on {recordedOn}. "
        + $"It stays in force until {rememberUntil}. You can stop this in Settings.";

    public static string Sha256Hex => Sha256HexOf(Text);

    public static string Fingerprint(GenerateReportRequest request)
    {
        var members = string.Join(',',
            (request.CardiMemberIds ?? []).OrderBy(id => id).Select(id => id.ToString("N")));

        var canonical = string.Join('|',
            members,
            request.DateRangeFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            request.DateRangeTo.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ((int)request.Format).ToString(CultureInfo.InvariantCulture),
            Flag(request.IncludeMetrics),
            Flag(request.IncludeTrends),
            Flag(request.IncludeAlerts),
            Flag(request.IncludeJournals),
            Flag(request.IncludeNotices),
            Flag(request.IncludeDevices),
            request.JournalEntryDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
            request.JournalAudience is { } audience
                ? ((int)audience).ToString(CultureInfo.InvariantCulture)
                : "");

        return Sha256HexOf(canonical);
    }

    public static string Sha256HexOf(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string Flag(bool value) => value ? "1" : "0";
}
