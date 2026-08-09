using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;

namespace CardiTrack.Application.Services;

/// <inheritdoc cref="ICardiMemberAccessService"/>
public class CardiMemberAccessService : ICardiMemberAccessService
{
    /// <summary>
    /// Deliberately identical to the message used when a CardiMember genuinely does not exist,
    /// so an unauthorised caller cannot distinguish "not yours" from "no such member".
    /// </summary>
    internal const string DeniedMessage = "CardiMember not found";

    private readonly IUnitOfWork _unitOfWork;

    public CardiMemberAccessService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<bool> HasViewAccessAsync(
        Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default)
    {
        var viewable = await LoadViewableMemberIdsAsync(requestingUserId);
        return viewable.Contains(cardiMemberId);
    }

    public async Task<IReadOnlyCollection<Guid>> GetViewableMemberIdsAsync(
        Guid requestingUserId, CancellationToken ct = default)
        => await LoadViewableMemberIdsAsync(requestingUserId);

    public Task RequireViewAccessAsync(
        Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default)
        => RequireViewAccessAsync(requestingUserId, new[] { cardiMemberId }, ct);

    public async Task RequireViewAccessAsync(
        Guid requestingUserId, IReadOnlyCollection<Guid> cardiMemberIds, CancellationToken ct = default)
    {
        if (cardiMemberIds.Count == 0)
            return;

        var viewable = await LoadViewableMemberIdsAsync(requestingUserId);
        if (cardiMemberIds.Any(id => !viewable.Contains(id)))
            throw new KeyNotFoundException(DeniedMessage);
    }

    public async Task RequireManageAccessAsync(
        Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default)
    {
        if (requestingUserId == Guid.Empty)
            throw new KeyNotFoundException(DeniedMessage);

        var links = await _unitOfWork.UserCardiMembers.GetByUserIdAsync(requestingUserId);
        var manages = links.Any(l =>
            l.CardiMemberId == cardiMemberId && l.IsActive && l.CanViewHealthData && l.IsPrimaryCaregiver);
        if (!manages)
            throw new KeyNotFoundException(DeniedMessage);
    }

    /// <summary>
    /// One repository round-trip regardless of how many members are being checked — the link
    /// set is small (a caregiver watches a handful of members) and callers such as report
    /// generation check several ids at once.
    /// </summary>
    private async Task<HashSet<Guid>> LoadViewableMemberIdsAsync(Guid requestingUserId)
    {
        if (requestingUserId == Guid.Empty)
            return [];

        var links = await _unitOfWork.UserCardiMembers.GetByUserIdAsync(requestingUserId);
        return links
            .Where(l => l.IsActive && l.CanViewHealthData)
            .Select(l => l.CardiMemberId)
            .ToHashSet();
    }
}
