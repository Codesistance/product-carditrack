using CardiTrack.Domain.Common;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// One caregiver's request for an exported health record, from queued to downloadable to expired.
/// </summary>
/// <remarks>
/// <para>
/// The row holds the request and its outcome; the rendered bytes live in the export bucket under
/// <see cref="ObjectName"/> (docs/infrastructure.md: "Files (report exports, …) live in Google
/// Cloud Storage buckets — never in the database"). Splitting them that way is what makes a report
/// survive an API restart: the previous design kept status and content as two distributed-cache
/// entries on a one-hour TTL, so a deploy mid-generation lost the job and a deploy after it lost a
/// finished report the caregiver had not downloaded yet.
/// </para>
/// <para>
/// A report is identified to clients by <see cref="Common.BaseEntity.Id"/> rendered in compact
/// <c>"N"</c> form — the published contract, unchanged from the cache-keyed design. That id is a
/// bearer-style handle, so <see cref="OwnerUserId"/> is what actually protects the content:
/// every read is scoped by it, and someone else's report reads as "no such report".
/// </para>
/// </remarks>
public class Report : BaseEntity
{
    /// <summary>The user who asked for the export. The only user who may see or download it.</summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>Which CardiMembers the export covers. Every id was access-checked before the row was written.</summary>
    public List<Guid> CardiMemberIds { get; set; } = [];

    public ReportFormat Format { get; set; }

    public ReportStatus Status { get; set; } = ReportStatus.Pending;

    public DateOnly DateRangeFrom { get; set; }

    public DateOnly DateRangeTo { get; set; }

    /// <summary>Optional caregiver-supplied title, rendered into the PDF cover.</summary>
    public string? Title { get; set; }

    /// <summary>
    /// Object name in the export bucket, <c>reports/{ownerUserId}/{id:N}.{ext}</c>. Null until
    /// rendering succeeds — the row is written first so a crashed generation leaves a
    /// <see cref="ReportStatus.Pending"/> row the cleanup worker can fail out, not a silent gap.
    /// </summary>
    public string? ObjectName { get; set; }

    /// <summary>Serving content type for the download, e.g. <c>application/fhir+json</c>. Null until ready.</summary>
    public string? ContentType { get; set; }

    /// <summary>Filename offered to the client. Null until ready.</summary>
    public string? FileName { get; set; }

    public long? FileSizeBytes { get; set; }

    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// When the object and this row become reapable. Set at queue time so the expiry a caregiver
    /// was told about does not depend on when generation happened to finish.
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Caregiver-facing failure text for <see cref="ReportStatus.Failed"/>. Deliberately generic —
    /// the diagnostic detail belongs in the logs, not in a response the requester might forward.
    /// </summary>
    public string? Error { get; set; }
}
