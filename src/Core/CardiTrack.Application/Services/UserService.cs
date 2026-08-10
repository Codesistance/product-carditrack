using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Exceptions;
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
        // The unique index on Email would reject a second identity claiming the same
        // address anyway, but as an opaque 500 — surface it as a conflict instead.
        await ThrowIfEmailOwnedElsewhereAsync(request.Email, request.Auth0UserId);

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

        try
        {
            await _unitOfWork.SaveChangesAsync();
        }
        catch
        {
            // A concurrent request can race past the check above and win the insert;
            // the unique index on Email then fails this one. Re-check who owns the
            // email now — a real conflict becomes 409, anything else is a real failure.
            await ThrowIfEmailOwnedElsewhereAsync(request.Email, request.Auth0UserId);
            throw;
        }

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

    private async Task ThrowIfEmailOwnedElsewhereAsync(string email, string auth0UserId)
    {
        var owner = await _unitOfWork.Users.GetByEmailAsync(email);
        if (owner is not null && owner.Auth0UserId != auth0UserId)
            throw new DuplicateEmailException("An account with this email already exists.");
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

    public async Task<bool> HasDismissedHealthDataDisclosureAsync(string auth0UserId)
    {
        var user = await _unitOfWork.Users.GetByAuth0UserIdAsync(auth0UserId);
        return user?.HealthDataDisclosureDismissedDate != null;
    }

    public async Task<bool> DismissHealthDataDisclosureAsync(string auth0UserId)
    {
        var user = await _unitOfWork.Users.GetByAuth0UserIdAsync(auth0UserId);
        if (user == null) return false;

        // Keep the first acknowledgment timestamp — it is the compliance-relevant one
        if (user.HealthDataDisclosureDismissedDate != null) return true;

        user.HealthDataDisclosureDismissedDate = DateTime.UtcNow;
        await _unitOfWork.SaveChangesAsync();
        return true;
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
        var cardiMembers = (await _unitOfWork.UserCardiMembers.GetByUserIdAsync(userId)).ToList();

        var hasDeviceConnected = await _unitOfWork.DeviceConnections
            .AnyActiveForCardiMembersAsync(cardiMembers.Select(link => link.CardiMemberId));

        var status = new OnboardingStatusResponse
        {
            HasOrganization = organization != null,
            HasUserAccount = true,
            HasCardiMember = cardiMembers.Any(),
            HasDeviceConnected = hasDeviceConnected,
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
