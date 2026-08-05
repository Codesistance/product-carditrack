using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Services;

public class OnboardingService : IOnboardingService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ISubscriptionService _subscriptionService;

    public OnboardingService(IUnitOfWork unitOfWork, ISubscriptionService subscriptionService)
    {
        _unitOfWork = unitOfWork;
        _subscriptionService = subscriptionService;
    }

    public async Task<OnboardingSetupResponse> SetupAsync(
        OnboardingSetupRequest request,
        string auth0UserId,
        bool? emailVerified)
    {
        // A retry after a lost response must return the already-created account
        // instead of provisioning a second organization.
        var existingUser = await _unitOfWork.Users.GetByAuth0UserIdAsync(auth0UserId);
        if (existingUser is not null)
        {
            var existingOrg = await _unitOfWork.Organizations.GetWithSubscriptionAsync(existingUser.OrganizationId);
            return new OnboardingSetupResponse
            {
                Organization = existingOrg is not null
                    ? MapOrganization(existingOrg)
                    : new OrganizationResponse { Id = existingUser.OrganizationId },
                User = MapUser(existingUser)
            };
        }

        var organization = new Organization
        {
            Name = request.Organization.Name,
            Type = request.Organization.Type,
            IsActive = true
        };
        await _unitOfWork.Organizations.AddAsync(organization);
        await _subscriptionService.CreateTrialSubscriptionAsync(organization.Id, request.Organization.Type);

        var user = new User
        {
            Auth0UserId = auth0UserId,
            Email = request.User.Email,
            Name = request.User.Name,
            Phone = request.User.Phone,
            Role = request.User.Role,
            Locale = request.User.Locale,
            TimeZoneId = request.User.TimeZoneId,
            OrganizationId = organization.Id,
            IsActive = true,
            // Real claim from the access token (via the tenant's post-login Action).
            // Absent claim => unverified until a later login proves otherwise.
            EmailVerified = emailVerified ?? false
        };
        await _unitOfWork.Users.AddAsync(user);

        // Single SaveChanges = single database transaction: the organization, its
        // trial subscription, and the user commit together or not at all, so no
        // failure mode can leave an orphaned organization behind.
        await _unitOfWork.SaveChangesAsync();

        var orgWithSubscription = await _unitOfWork.Organizations.GetWithSubscriptionAsync(organization.Id);
        return new OnboardingSetupResponse
        {
            Organization = orgWithSubscription is not null ? MapOrganization(orgWithSubscription) : MapOrganization(organization),
            User = MapUser(user)
        };
    }

    private static OrganizationResponse MapOrganization(Organization organization) => new()
    {
        Id = organization.Id,
        Name = organization.Name,
        Type = organization.Type,
        IsActive = organization.IsActive,
        CreatedDate = organization.CreatedDate,
        Subscription = organization.Subscription != null ? new SubscriptionResponse
        {
            Id = organization.Subscription.Id,
            Tier = organization.Subscription.Tier,
            Status = organization.Subscription.Status,
            StartDate = organization.Subscription.StartDate,
            TrialEndDate = organization.Subscription.TrialEndDate,
            MaxCardiMembers = organization.Subscription.MaxCardiMembers,
            MaxUsers = organization.Subscription.MaxUsers
        } : null
    };

    private static UserResponse MapUser(User user) => new()
    {
        Id = user.Id,
        Email = user.Email,
        Name = user.Name,
        Phone = user.Phone,
        Role = user.Role,
        OrganizationId = user.OrganizationId,
        IsActive = user.IsActive,
        Locale = user.Locale,
        TimeZoneId = user.TimeZoneId,
        CreatedDate = user.CreatedDate
    };
}
