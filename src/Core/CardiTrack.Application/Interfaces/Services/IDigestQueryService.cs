using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Application.Interfaces.Services;

/// <summary>Read side of the daily digests, relationship-scoped like every health-data read.</summary>
public interface IDigestQueryService
{
    /// <summary>
    /// The member's family digest for <paramref name="localDate"/>, or the most recent one when
    /// no date is given. Null when none has been generated yet — which the first days of a new
    /// member legitimately are.
    /// </summary>
    Task<DigestResponse?> GetDigestAsync(
        Guid requestingUserId, Guid cardiMemberId, DateOnly? localDate, CancellationToken ct = default);
}
