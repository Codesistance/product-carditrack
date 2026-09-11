using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.DTOs.Requests;

/// <summary>
/// The same snapshot a generate call will send, plus how the caregiver proved it
/// and that they accepted responsibility. Password is verified on the device
/// against Auth0 and is never sent here — CardiTrack does not handle credentials
/// (docs/technical/auth0_integration.md).
/// </summary>
public class RecordExportConsentRequest
{
    public required IReadOnlyList<Guid> CardiMemberIds { get; init; }
    public required DateOnly DateRangeFrom { get; init; }
    public required DateOnly DateRangeTo { get; init; }
    public required ReportFormat Format { get; init; }

    public bool IncludeMetrics { get; init; } = true;
    public bool IncludeTrends { get; init; } = true;
    public bool IncludeAlerts { get; init; } = true;
    public bool IncludeJournals { get; init; }
    public bool IncludeNotices { get; init; }
    public bool IncludeDevices { get; init; }

    public DateOnly? JournalEntryDate { get; init; }
    public DigestAudience? JournalAudience { get; init; }

    public required ExportConsentMethod Method { get; init; }

    public bool AcceptedResponsibility { get; init; }

    /// <summary>
    /// How long this confirmation may authorize later exports. Omitted or
    /// <see cref="ExportConsentRememberFor.ThisExport"/> keeps today's two-minute token.
    /// </summary>
    public ExportConsentRememberFor RememberFor { get; init; } = ExportConsentRememberFor.ThisExport;
}
