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

/// <summary>
/// Whether this caregiver's plan includes health data export, and what to tell them if not.
/// </summary>
/// <remarks>
/// Exists so the export entry point can offer the upgrade instead of a form that would be
/// refused with a 402 once it is filled in. It is a convenience for the UI, never the gate:
/// <c>POST /api/v1/reports</c> checks entitlement itself, because a client is not an authority
/// on what its user has paid for.
/// </remarks>
public class ReportAvailabilityResponse
{
    public required bool Available { get; init; }

    /// <summary>Copy to show when <see cref="Available"/> is false; null when it is true.</summary>
    public string? Message { get; init; }

    /// <summary>The tier export needs, for a client that wants to name it in its own copy.</summary>
    public SubscriptionTier RequiredTier { get; init; }
}
