using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <inheritdoc cref="IGuestFamilyProvisioner"/>
public class GuestFamilyProvisioner : IGuestFamilyProvisioner
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ISubscriptionService _subscriptions;
    private readonly IFamilyWriteGuard _guard;

    public GuestFamilyProvisioner(
        IUnitOfWork unitOfWork, ISubscriptionService subscriptions, IFamilyWriteGuard guard)
    {
        _unitOfWork = unitOfWork;
        _subscriptions = subscriptions;
        _guard = guard;
    }

    public async Task<Guid> ResolveHomeOrganizationAsync(Guid userId, CancellationToken ct = default)
    {
        const string Gone = "We couldn't find your account — please sign in again.";

        // Read once without a lock: the overwhelmingly common answer is "they already have one",
        // and establishing that should not cost a transaction.
        if ((await _unitOfWork.Users.GetByIdAsync(userId) ?? throw new KeyNotFoundException(Gone))
            .OrganizationId is { } already)
        {
            return already;
        }

        await _unitOfWork.BeginTransactionAsync();
        try
        {
            if (!await _guard.HoldAccountAsync(userId, ct))
                throw new KeyNotFoundException(Gone);

            // Re-read under the lock, past the change tracker. The read above happened while
            // nobody held anything, so it can only say what was true then — and two first-member
            // requests arriving together would both see null and both create a family, leaving one
            // of them with a trial, a subscription and a membership, and nothing pointing at it.
            _unitOfWork.ClearTracking();
            var user = await _unitOfWork.Users.GetByIdAsync(userId)
                ?? throw new KeyNotFoundException(Gone);

            if (user.OrganizationId is { } existing)
            {
                await _unitOfWork.CommitTransactionAsync();
                return existing;
            }

            // Named from the person, because there is nobody to ask at this point: they are part-way
            // through adding a relative, and interrupting that to ask what to call a family they did
            // not know they were creating would be a worse trade than a name they can change.
            var organization = new Organization
            {
                Name = FamilyNameFor(user.Name),
                Type = OrganizationType.Family,
                FamilyId = FamilyIdentifier.Mint(),
                IsActive = true,
            };

            await _unitOfWork.Organizations.AddAsync(organization);
            await _subscriptions.CreateTrialSubscriptionAsync(organization.Id, OrganizationType.Family);

            // Their home family, and they run it — the same pair onboarding writes for somebody who
            // started one at signup.
            user.OrganizationId = organization.Id;
            user.UpdatedDate = DateTime.UtcNow;
            _unitOfWork.Users.Update(user);

            await _unitOfWork.UserOrganizations.AddAsync(new UserOrganization
            {
                UserId = user.Id,
                OrganizationId = organization.Id,
                Role = UserRole.Admin,
            });

            await _unitOfWork.SaveChangesAsync();
            await _unitOfWork.CommitTransactionAsync();

            return organization.Id;
        }
        finally
        {
            // A no-op once committed. What it catches is a throw anywhere above, which would
            // otherwise leave a half-built family behind.
            await _unitOfWork.RollbackTransactionAsync();
        }
    }

    /// <summary>
    /// "Jane Okafor" becomes "The Okafors"; a single name becomes "Jane's family". Not clever, and
    /// deliberately so — it only has to be recognisable enough that somebody renaming it knows
    /// which family they are renaming.
    /// </summary>
    private static string FamilyNameFor(string userName)
    {
        var parts = userName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return "My family";

        if (parts.Length == 1)
            return $"{parts[0]}'s family";

        var surname = parts[^1];
        return surname.EndsWith('s') ? $"The {surname}" : $"The {surname}s";
    }
}
