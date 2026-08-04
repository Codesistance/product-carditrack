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
}
