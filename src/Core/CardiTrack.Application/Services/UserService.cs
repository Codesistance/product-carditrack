using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Services;

public class UserService : IUserService
{
    private readonly IUnitOfWork _unitOfWork;

    public UserService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<UserResponse> CreateUserAsync(CreateUserRequest request)
    {
        var user = new User
        {
            Auth0UserId = request.Auth0UserId,
            Email = request.Email,
            Name = request.Name,
            Phone = request.Phone,
            Role = request.Role,
            OrganizationId = request.OrganizationId,
            IsActive = true,
            // Real claim from the access token (via the tenant's post-login Action).
            // Absent claim => unverified until a later login proves otherwise.
            EmailVerified = request.EmailVerified ?? false
        };

        await _unitOfWork.Users.AddAsync(user);
        await _unitOfWork.SaveChangesAsync();

        return new UserResponse
        {
            Id = user.Id,
            Email = user.Email,
            Name = user.Name,
            Phone = user.Phone,
            Role = user.Role,
            OrganizationId = user.OrganizationId,
            IsActive = user.IsActive,
            CreatedDate = user.CreatedDate
        };
    }

    public async Task<User?> GetByAuth0UserIdAsync(string auth0UserId)
    {
        return await _unitOfWork.Users.GetByAuth0UserIdAsync(auth0UserId);
    }

    public async Task<UserResponse?> GetByIdAsync(Guid userId)
    {
        var user = await _unitOfWork.Users.GetByIdAsync(userId);
        if (user == null) return null;

        return new UserResponse
        {
            Id = user.Id,
            Email = user.Email,
            Name = user.Name,
            Phone = user.Phone,
            Role = user.Role,
            OrganizationId = user.OrganizationId,
            IsActive = user.IsActive,
            CreatedDate = user.CreatedDate
        };
    }

    public async Task UpdateLastLoginAsync(Guid userId)
    {
        await _unitOfWork.Users.UpdateLastLoginAsync(userId);
        await _unitOfWork.SaveChangesAsync();
    }

    public async Task<OnboardingStatusResponse> GetOnboardingStatusAsync(Guid userId, bool? emailVerifiedClaim = null)
    {
        var user = await _unitOfWork.Users.GetByIdAsync(userId);
        if (user == null)
        {
            return new OnboardingStatusResponse
            {
                HasOrganization = false,
                HasUserAccount = false,
                CurrentStep = 1,
                NextStepMessage = "Let's create your organization"
            };
        }

        // The app calls this endpoint on every launch, making it the natural point to
        // sync verification state once the user has clicked Auth0's email link.
        if (emailVerifiedClaim.HasValue && user.EmailVerified != emailVerifiedClaim.Value)
        {
            user.EmailVerified = emailVerifiedClaim.Value;
            await _unitOfWork.SaveChangesAsync();
        }

        var organization = await _unitOfWork.Organizations.GetByIdAsync(user.OrganizationId);
        var cardiMembers = await _unitOfWork.UserCardiMembers.GetByUserIdAsync(userId);

        var status = new OnboardingStatusResponse
        {
            HasOrganization = organization != null,
            HasUserAccount = true,
            HasCardiMember = cardiMembers.Any(),
            HasDeviceConnected = false, // TODO: Check device connections
            HasNotificationPreferences = false, // TODO: Check notification prefs
            CurrentStep = 2
        };

        if (!status.HasCardiMember)
        {
            status.NextStepMessage = "Let's add your first CardiMember";
            status.CurrentStep = 5;
        }
        else if (!status.HasDeviceConnected)
        {
            status.NextStepMessage = "Now let's connect a health device";
            status.CurrentStep = 6;
        }
        else if (!status.HasNotificationPreferences)
        {
            status.NextStepMessage = "Choose how you'd like to be notified";
            status.CurrentStep = 7;
        }
        else
        {
            status.IsOnboardingComplete = true;
            status.CurrentStep = 7;
            status.NextStepMessage = "You're all set — welcome to CardiTrack!";
        }

        return status;
    }
}
