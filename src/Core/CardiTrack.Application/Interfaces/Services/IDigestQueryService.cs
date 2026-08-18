using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Interfaces.Services;

/// <summary>Read side of the generated summaries, relationship-scoped like every health-data read.</summary>
/// <remarks>
/// Both reads default to <see cref="DigestAudience.Family"/>, which is what every caller asked for
/// before day reviews existed — so an omitted audience is the behaviour those callers already have,
/// and adding the parameter changed no existing response.
/// </remarks>
public interface IDigestQueryService
{
    /// <summary>
    /// The member's current summary for the audience, or the latest one describing
    /// <paramref name="localDate"/> when a date is given. Null when none has been generated yet —
    /// which the first days of a new member legitimately are.
    /// </summary>
    Task<DigestResponse?> GetDigestAsync(
        Guid requestingUserId,
        Guid cardiMemberId,
        DateOnly? localDate,
        DigestAudience audience = DigestAudience.Family,
        CancellationToken ct = default);

    /// <summary>
    /// The member's summaries for the audience, newest first. For
    /// <see cref="DigestAudience.Family"/> that is the current one and the recomputations behind it,
    /// all of which may describe the same day; for <see cref="DigestAudience.DayReview"/> it is one
    /// entry per day, because a review is written once. Empty when none has been generated yet.
    /// </summary>
    Task<IReadOnlyList<DigestResponse>> GetHistoryAsync(
        Guid requestingUserId,
        Guid cardiMemberId,
        int limit,
        DigestAudience audience = DigestAudience.Family,
        CancellationToken ct = default);
}
