using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.DTOs.Responses;

public class ExportConsentResponse
{
    public required string ConsentToken { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>True when this token was minted from a standing grant rather than a fresh step-up.</summary>
    public bool Reused { get; init; }

    /// <summary>The standing grant this token came from, when <see cref="Reused"/>.</summary>
    public Guid? ReusedFromConsentId { get; init; }

    /// <summary>When the original confirmation was given, when <see cref="Reused"/>.</summary>
    public DateTimeOffset? OriginalConsentedAt { get; init; }

    /// <summary>When a standing grant, if any, stops authorizing later exports.</summary>
    public DateTimeOffset? RememberUntil { get; init; }

    /// <summary>
    /// Caregiver-facing copy naming the reuse. Set only when <see cref="Reused"/> so
    /// the client can show it verbatim.
    /// </summary>
    public string? ReuseNotice { get; init; }
}

/// <summary>One row on the Settings confirmation history.</summary>
public class ExportConsentHistoryItem
{
    public required Guid Id { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
    public ExportConsentMethod Method { get; init; }
    public ExportConsentRememberFor RememberFor { get; init; }
    public DateTimeOffset? RememberUntil { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
    public DateTimeOffset? ConsumedAt { get; init; }
    public bool Reused { get; init; }

        /// <summary>Standing grant still in force — Settings offers Stop.</summary>
    public bool CanRevoke { get; init; }

    /// <summary>
    /// Standing grant the next export may reuse (in force, and the wording
    /// they accepted is still the current policy).
    /// </summary>
    public bool CanReuse { get; init; }

    /// <summary>One line a caregiver can read without the enum names.</summary>
    public required string Summary { get; init; }
}
