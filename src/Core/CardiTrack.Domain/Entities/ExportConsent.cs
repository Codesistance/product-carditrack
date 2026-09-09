using CardiTrack.Domain.Common;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// One recorded "I accept responsibility" step-up for a health-data export.
/// </summary>
/// <remarks>
/// <para>
/// Append-only: a used or expired row is never updated except to stamp
/// <see cref="ConsumedAt"/> and <see cref="ReportId"/> when the matching
/// <c>POST /api/v1/reports</c> consumes it. Withdrawal is "do not export again",
/// not an edit of this row.
/// </para>
/// <para>
/// The id (compact <c>"N"</c> form) is the consent token the client must send
/// with the generate request. It is short-lived and single-use, and is bound
/// to the request fingerprint so a token minted for one member/period/section
/// set cannot authorize a different export.
/// </para>
/// </remarks>
public class ExportConsent : BaseEntity
{
    public Guid OwnerUserId { get; set; }

    public List<Guid> CardiMemberIds { get; set; } = [];

    public DateOnly DateRangeFrom { get; set; }

    public DateOnly DateRangeTo { get; set; }

    public ReportFormat Format { get; set; }

    public bool IncludeMetrics { get; set; }

    public bool IncludeTrends { get; set; }

    public bool IncludeAlerts { get; set; }

    public bool IncludeJournals { get; set; }

    public bool IncludeNotices { get; set; }

    public bool IncludeDevices { get; set; }

    public DateOnly? JournalEntryDate { get; set; }

    public DigestAudience? JournalAudience { get; set; }

    /// <summary>Policy text version the caregiver was shown, e.g. <c>export-responsibility-2026-09</c>.</summary>
    public string PolicyVersion { get; set; } = string.Empty;

    /// <summary>SHA-256 (hex) of the exact policy text shown. Not the text itself.</summary>
    public string PolicySha256 { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 (hex) of the canonical request snapshot. Generate must present the same snapshot.
    /// </summary>
    public string RequestFingerprint { get; set; } = string.Empty;

    public ExportConsentMethod Method { get; set; }

    public DateTime ExpiresAt { get; set; }

    /// <summary>When a generate call consumed this token. Null means it is still unused.</summary>
    public DateTime? ConsumedAt { get; set; }

    /// <summary>The report this consent authorized, stamped at consume time.</summary>
    public Guid? ReportId { get; set; }
}
