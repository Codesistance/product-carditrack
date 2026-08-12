using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Application.Interfaces.Services;

/// <summary>Read side of the generated summaries, relationship-scoped like every health-data read.</summary>
public interface IDigestQueryService
{
    /// <summary>
    /// The member's current family summary, or the latest one describing <paramref name="localDate"/>
    /// when a date is given. Null when none has been generated yet — which the first days of a new
    /// member legitimately are.
    /// </summary>
    Task<DigestResponse?> GetDigestAsync(
        Guid requestingUserId, Guid cardiMemberId, DateOnly? localDate, CancellationToken ct = default);

    /// <summary>
    /// The member's family summaries newest first — the current one and the recomputations behind
    /// it. Empty when none has been generated yet.
    /// </summary>
    Task<IReadOnlyList<DigestResponse>> GetHistoryAsync(
        Guid requestingUserId, Guid cardiMemberId, int limit, CancellationToken ct = default);
}
