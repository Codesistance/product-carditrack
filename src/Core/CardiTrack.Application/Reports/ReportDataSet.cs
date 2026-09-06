using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Reports;

/// <summary>
/// Everything one export covers, gathered once and handed to whichever renderer the caregiver
/// asked for.
/// </summary>
/// <remarks>
/// A single gather, not one per renderer: the PDF's narrative and its tables must describe the
/// same days, and a second query against a live database could disagree with the first. It also
/// keeps the format choice where it belongs — in rendering, not in data access.
/// </remarks>
/// <param name="Members">One entry per requested CardiMember, in request order. Members the
/// caller could not read never reach here: access is vetted before the report is queued.</param>
public record ReportDataSet(
    IReadOnlyList<ReportMemberData> Members,
    DateOnly From,
    DateOnly To,
    string? Title);

/// <summary>One member's slice of an export.</summary>
/// <param name="Devices">Connections that produced the readings — the FHIR <c>Device</c>
/// resources, and the PDF's provenance line. Device labels are caregiver free text
/// (docs/technical/data_protection_architecture.md §70) and so are never rendered; only the
/// device type is.</param>
public record ReportMemberData(
    CardiMember Member,
    IReadOnlyList<ActivityLog> ActivityLogs,
    IReadOnlyList<Alert> Alerts,
    IReadOnlyList<DeviceConnection> Devices);

/// <summary>
/// Which parts of the record the caregiver ticked on M1-17. Mirrors the request flags, so a
/// renderer never sees the transport DTO.
/// </summary>
public record ReportSections(
    bool IncludeMetrics,
    bool IncludeAlerts,
    bool IncludeDevices);

/// <summary>
/// A rendered export, ready to store and serve.
/// </summary>
/// <param name="Extension">Filename extension without the dot — also the object-name suffix in
/// the bucket, so an operator listing it can tell a PDF from a FHIR bundle.</param>
public record RenderedReport(byte[] Content, string ContentType, string Extension);
