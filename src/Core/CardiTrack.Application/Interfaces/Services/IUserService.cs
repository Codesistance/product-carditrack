using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Services;

public interface IUserService
{
    Task<User?> GetByAuth0UserIdAsync(string auth0UserId);
    Task<UserResponse?> GetByIdAsync(Guid userId);
    Task UpdateLastLoginAsync(Guid userId);
    Task<OnboardingStatusResponse> GetOnboardingStatusAsync(Guid userId, bool? emailVerifiedClaim = null);
    Task<bool> HasDismissedHealthDataDisclosureAsync(string auth0UserId);
    Task<bool> DismissHealthDataDisclosureAsync(string auth0UserId);

    /// <summary>
    /// Where this account stands with respect to deletion.
    /// </summary>
    Task<AccountDeletionStatusResponse?> GetDeletionStatusAsync(string auth0UserId);

    /// <summary>
    /// Records a request to delete this account. Idempotent: a second request returns the first
    /// one's status rather than restarting the 30 days.
    /// </summary>
    /// <remarks>
    /// The request is all this does. Nothing is erased here — the window exists so a caregiver can
    /// take it back, and M6's worker is what eventually carries it out; until that ships, the
    /// manual runbook does. What a client must do on a successful request is sign the caregiver
    /// out, because an account awaiting deletion should not go on monitoring anyone.
    /// </remarks>
    Task<AccountDeletionStatusResponse?> RequestDeletionAsync(string auth0UserId);

    /// <summary>
    /// Calls off an outstanding deletion request. Returns the resulting status, or null when the
    /// account is not one this platform knows.
    /// </summary>
    Task<AccountDeletionStatusResponse?> CancelDeletionAsync(string auth0UserId);

    /// <summary>
    /// Sets the caller's IANA time zone. The column defaults to <c>"UTC"</c>, derived from
    /// <c>Accept-Language</c> rather than chosen — and UTC observes no daylight saving, so every
    /// statement CardiTrack makes about "today" or "this morning" is made in the wrong clock until
    /// this is set. Returns false when the id is not one the platform recognises.
    /// </summary>
    Task<bool> UpdateTimeZoneAsync(Guid userId, string timeZoneId, CancellationToken ct = default);
}
