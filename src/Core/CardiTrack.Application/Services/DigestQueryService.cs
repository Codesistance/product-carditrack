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
        Guid requestingUserId, Guid cardiMemberId, DateOnly? localDate, CancellationToken ct = default)
    {
        await _access.RequireViewAccessAsync(requestingUserId, cardiMemberId, ct);

        var entry = localDate is { } date
            ? await _unitOfWork.Digests.GetLatestByDateAsync(cardiMemberId, date, DigestAudience.Family, ct)
            : await _unitOfWork.Digests.GetLatestAsync(cardiMemberId, DigestAudience.Family, ct);

        return entry is null ? null : ToResponse(entry);
    }

    public async Task<IReadOnlyList<DigestResponse>> GetHistoryAsync(
        Guid requestingUserId, Guid cardiMemberId, int limit, CancellationToken ct = default)
    {
        await _access.RequireViewAccessAsync(requestingUserId, cardiMemberId, ct);

        var entries = await _unitOfWork.Digests.GetHistoryAsync(
            cardiMemberId, DigestAudience.Family, Math.Clamp(limit, 1, MaxHistoryLimit), ct);

        return entries.Select(ToResponse).ToList();
    }

    private static DigestResponse ToResponse(DigestEntry entry) => new()
    {
        CardiMemberId = entry.CardiMemberId,
        LocalDate = entry.LocalDate,
        Audience = entry.Audience.ToString(),
        Headline = entry.Headline,
        Text = entry.Text,
        Suggestion = entry.Suggestion,
        GeneratedAtUtc = entry.GeneratedAtUtc,
    };
}
