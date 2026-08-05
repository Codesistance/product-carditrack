using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Application.Interfaces.Services;

public interface IOnboardingService
{
    /// <summary>
    /// Creates the organization, its trial subscription, and the user atomically.
    /// Idempotent: if the Auth0 user already has an account, the existing
    /// organization and user are returned instead of creating duplicates.
    /// </summary>
    Task<OnboardingSetupResponse> SetupAsync(
        OnboardingSetupRequest request,
        string auth0UserId,
        bool? emailVerified);
}
