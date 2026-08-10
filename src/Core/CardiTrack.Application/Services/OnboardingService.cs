using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Exceptions;
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

        // A different Auth0 identity may already own this email — e.g. a social login
        // that the tenant's account-linking Action didn't link (first registered by
        // password, Action failed open, or linking skipped). Refuse to fork a second
        // account for the same person; the unique index on Email would reject the
        // insert anyway, but as an opaque 500.
        await ThrowIfEmailOwnedElsewhereAsync(request.User.Email, auth0UserId, emailVerified);

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
        try
        {
            await _unitOfWork.SaveChangesAsync();
        }
        catch
        {
            // Concurrent retries race past the existence check above; the unique index
            // on Auth0UserId makes the loser's insert fail. If the account now exists,
            // the other request won — return it. A race on the Email index (a second
            // identity claiming the same address) is a conflict, not a server fault.
            var concurrentUser = await _unitOfWork.Users.GetByAuth0UserIdAsync(auth0UserId);
            if (concurrentUser is null)
            {
                await ThrowIfEmailOwnedElsewhereAsync(request.User.Email, auth0UserId, emailVerified);
                throw;
            }

            var concurrentOrg = await _unitOfWork.Organizations.GetWithSubscriptionAsync(concurrentUser.OrganizationId);
            return new OnboardingSetupResponse
            {
                Organization = concurrentOrg is not null
                    ? MapOrganization(concurrentOrg)
                    : new OrganizationResponse { Id = concurrentUser.OrganizationId },
                User = MapUser(concurrentUser)
            };
        }

        var orgWithSubscription = await _unitOfWork.Organizations.GetWithSubscriptionAsync(organization.Id);
        return new OnboardingSetupResponse
        {
            Organization = orgWithSubscription is not null ? MapOrganization(orgWithSubscription) : MapOrganization(organization),
            User = MapUser(user)
        };
    }

    private async Task ThrowIfEmailOwnedElsewhereAsync(string email, string auth0UserId, bool? emailVerified)
    {
        var owner = await _unitOfWork.Users.GetByEmailAsync(email);
        if (owner is null || owner.Auth0UserId == auth0UserId)
            return;

        // Only a token that has proven this email gets the linkable guidance; an
        // unverified claim on the address learns nothing about the other account.
        throw new DuplicateEmailException(emailVerified == true
            ? "You already have a CardiTrack account with this email under a different sign-in "
              + "method. Sign in the way you first registered, and your sign-in methods will "
              + "be linked automatically."
            : "An account with this email already exists.");
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
