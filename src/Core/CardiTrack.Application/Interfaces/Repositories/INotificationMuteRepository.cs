using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

public interface INotificationMuteRepository : IRepository<NotificationMute>
{
    Task<IReadOnlyList<NotificationMute>> GetByUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// The existing mute for exactly this scope, so dismissing twice updates one row rather than
    /// racing the unique index.
    /// </summary>
    Task<NotificationMute?> FindForScopeAsync(
        Guid userId, string? ruleCode, Domain.Enums.NotificationCategory? category,
        Guid? cardiMemberId, CancellationToken ct = default);

    /// <summary>"Show me everything again" — clears every mute the user holds.</summary>
    Task<int> ClearAllForUserAsync(Guid userId, CancellationToken ct = default);
}
