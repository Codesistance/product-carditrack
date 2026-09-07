using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.DTOs.Responses;

public class ReportQueuedResponse
{
    public required string ReportId { get; init; }
    public ReportStatus Status { get; init; } = ReportStatus.Pending;
    public int EstimatedReadyInSeconds { get; init; }
    public required string StatusUrl { get; init; }
}

public class ReportStatusResponse
{
    public required string ReportId { get; init; }
    public required ReportStatus Status { get; init; }
    public int? ProgressPercent { get; init; }
    public ReportFormat? Format { get; init; }
    public string? ContentType { get; init; }
    public long? FileSizeBytes { get; init; }
    public string? DownloadUrl { get; init; }
    public DateTimeOffset? DownloadExpiresAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? Error { get; init; }
    public ReportMetadata? Metadata { get; init; }
}

public class ReportMetadata
{
    public IReadOnlyList<string> CardiMembers { get; init; } = [];
    public DateOnly DateRangeFrom { get; init; }
    public DateOnly DateRangeTo { get; init; }

    // PDF/CSV
    public IReadOnlyList<string>? Sections { get; init; }

    // FHIR R4
    public string? FhirProfile { get; init; }
    public IReadOnlyList<string>? FhirResources { get; init; }
}

