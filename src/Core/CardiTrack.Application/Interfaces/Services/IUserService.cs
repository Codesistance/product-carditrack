using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Services;

public interface IUserService
{
    Task<UserResponse> CreateUserAsync(CreateUserRequest request);
    Task<User?> GetByAuth0UserIdAsync(string auth0UserId);
    Task<UserResponse?> GetByIdAsync(Guid userId);
    Task UpdateLastLoginAsync(Guid userId);
    Task<OnboardingStatusResponse> GetOnboardingStatusAsync(Guid userId, bool? emailVerifiedClaim = null);
    Task<bool> HasDismissedHealthDataDisclosureAsync(string auth0UserId);
    Task<bool> DismissHealthDataDisclosureAsync(string auth0UserId);

    /// <summary>
    /// Sets the caller's IANA time zone. The column defaults to <c>"UTC"</c>, derived from
    /// <c>Accept-Language</c> rather than chosen — and UTC observes no daylight saving, so every
    /// statement CardiTrack makes about "today" or "this morning" is made in the wrong clock until
    /// this is set. Returns false when the id is not one the platform recognises.
    /// </summary>
    Task<bool> UpdateTimeZoneAsync(Guid userId, string timeZoneId, CancellationToken ct = default);
}
