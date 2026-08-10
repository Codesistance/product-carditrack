using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

public class DigestQueryService : IDigestQueryService
{
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
            ? await _unitOfWork.Digests.GetByDateAsync(cardiMemberId, date, DigestAudience.Family, ct)
            : await _unitOfWork.Digests.GetLatestAsync(cardiMemberId, DigestAudience.Family, ct);

        return entry is null
            ? null
            : new DigestResponse
            {
                CardiMemberId = entry.CardiMemberId,
                LocalDate = entry.LocalDate,
                Audience = entry.Audience.ToString(),
                Text = entry.Text,
                GeneratedAtUtc = entry.GeneratedAtUtc,
            };
    }
}
