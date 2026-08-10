using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// The caregiver-facing side of the notification engine: reading the inbox and acting on an item.
/// </summary>
public interface INotificationService
{
    Task<NotificationListResponse> GetInboxAsync(
        Guid requestingUserId,
        NotificationState? state,
        NotificationCategory? category,
        Guid? cardiMemberId,
        bool? owned,
        int limit,
        int offset,
        CancellationToken ct = default);

    /// <summary>Badge count plus the dashboard card slots — one call on app launch.</summary>
    Task<NotificationSummaryResponse> GetSummaryAsync(Guid requestingUserId, CancellationToken ct = default);

    /// <summary>Records that the user has laid eyes on it. The denominator of the comply funnel.</summary>
    Task MarkSeenAsync(Guid requestingUserId, Guid notificationId, CancellationToken ct = default);

    /// <summary>
    /// Hides it until the duration elapses, clamped to the rule's maximum. Safety rules cap at 72
    /// hours however long the caller asks for.
    /// </summary>
    Task<NotificationResponse> SnoozeAsync(
        Guid requestingUserId, Guid notificationId, TimeSpan? duration, CancellationToken ct = default);

    /// <summary>
    /// "Don't ask again" — writes a mute and resolves the row.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown for a Safety-class rule unless <paramref name="acknowledgedConsequence"/> is set. Those
    /// cannot be silenced casually: the caller must have shown the user what they are giving up.
    /// </exception>
    Task DismissAsync(
        Guid requestingUserId, Guid notificationId, bool acknowledgedConsequence, CancellationToken ct = default);

    Task<IReadOnlyList<NotificationMuteResponse>> GetMutesAsync(
        Guid requestingUserId, CancellationToken ct = default);

    Task RemoveMuteAsync(Guid requestingUserId, Guid muteId, CancellationToken ct = default);

    /// <summary>"Show me everything again" — clears every mute the user holds.</summary>
    Task<int> ResetMutesAsync(Guid requestingUserId, CancellationToken ct = default);
}
