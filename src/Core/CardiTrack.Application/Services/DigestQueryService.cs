using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

public class DigestQueryService : IDigestQueryService
{
    /// <summary>
    /// Ceiling on a history page. Summaries are recomputed as data lands rather than once a day,
    /// so an uncapped read is a genuinely unbounded one.
    /// </summary>
    public const int MaxHistoryLimit = 50;

    private readonly IUnitOfWork _unitOfWork;
    private readonly ICardiMemberAccessService _access;

    public DigestQueryService(IUnitOfWork unitOfWork, ICardiMemberAccessService access)
    {
        _unitOfWork = unitOfWork;
        _access = access;
    }

    public async Task<DigestResponse?> GetDigestAsync(
        Guid requestingUserId,
        Guid cardiMemberId,
        DateOnly? localDate,
        DigestAudience audience = DigestAudience.Family,
        CancellationToken ct = default)
    {
        await _access.RequireViewAccessAsync(requestingUserId, cardiMemberId, ct);

        var entry = localDate is { } date
            ? await _unitOfWork.Digests.GetLatestByDateAsync(cardiMemberId, date, audience, ct)
            : await _unitOfWork.Digests.GetLatestAsync(cardiMemberId, audience, ct);

        return entry is null ? null : ToResponse(entry);
    }

    public async Task<IReadOnlyList<DigestResponse>> GetHistoryAsync(
        Guid requestingUserId,
        Guid cardiMemberId,
        int limit,
        DigestAudience audience = DigestAudience.Family,
        CancellationToken ct = default)
    {
        await _access.RequireViewAccessAsync(requestingUserId, cardiMemberId, ct);

        var entries = await _unitOfWork.Digests.GetHistoryAsync(
            cardiMemberId, audience, Math.Clamp(limit, 1, MaxHistoryLimit), ct);

        return entries.Select(ToResponse).ToList();
    }

    /// <summary>
    /// The audience a caller named, or null when they named one that does not exist. Wearer is
    /// rejected alongside a typo rather than returning an empty list: wearer summaries are not
    /// generated yet, and an endpoint that answers "none" to a question it will one day answer
    /// differently teaches a client the wrong thing about a member who simply has none.
    /// </summary>
    public static DigestAudience? ParseAudience(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return DigestAudience.Family;

        return value.Trim().ToLowerInvariant() switch
        {
            "family" => DigestAudience.Family,
            "dayreview" or "day-review" => DigestAudience.DayReview,
            _ => null,
        };
    }

    private static DigestResponse ToResponse(DigestEntry entry) => new()
    {
        CardiMemberId = entry.CardiMemberId,
        LocalDate = entry.LocalDate,
        Audience = entry.Audience.ToString(),
        Headline = entry.Headline,
        Text = entry.Text,
        Suggestion = entry.Suggestion,
        Urgency = ToUrgencyWireValue(entry.Urgency),
        GeneratedAtUtc = entry.GeneratedAtUtc,
    };

    /// <summary>
    /// The hyphenated wire vocabulary (watch / check-in / concerning / act-now) rather than a bare
    /// <c>ToString().ToLowerInvariant()</c> — the same words the AI prompt itself asks for, so a
    /// client matching on this string is matching the vocabulary the model was actually given.
    /// </summary>
    private static string? ToUrgencyWireValue(DigestUrgency? urgency) => urgency switch
    {
        DigestUrgency.Watch => "watch",
        DigestUrgency.CheckIn => "check-in",
        DigestUrgency.Concerning => "concerning",
        DigestUrgency.ActNow => "act-now",
        _ => null,
    };
}
