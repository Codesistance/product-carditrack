using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

public class UserService : IUserService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly INotificationGapResolver _gapResolver;

    public UserService(IUnitOfWork unitOfWork, INotificationGapResolver gapResolver)
    {
        _unitOfWork = unitOfWork;
        _gapResolver = gapResolver;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateTimeZoneAsync(Guid userId, string timeZoneId, CancellationToken ct = default)
    {
        // Validated against the platform's database rather than a regex: an id we cannot resolve
        // would fail later, inside whatever tries to render a local time, far from the cause.
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out _))
            return false;

        var user = await _unitOfWork.Users.GetByIdAsync(userId);
        if (user is null || !user.IsActive)
            throw new KeyNotFoundException("User not found");

        user.TimeZoneId = timeZoneId;
        user.UpdatedDate = DateTime.UtcNow;
        _unitOfWork.Users.Update(user);
        await _unitOfWork.SaveChangesAsync();

        await _gapResolver.ResolveForUserAsync(userId, ct);
        return true;
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

    /// <summary>
    /// How long an account awaiting deletion is kept before its data is erased.
    /// </summary>
    /// <remarks>
    /// Thirty days because that is what the published privacy policy promises — "deleted within 30
    /// days of a verified request" — and the number is spent as a grace period rather than merely
    /// allowed as a deadline. Changing it changes a published commitment, not just a constant.
    /// </remarks>
    public static readonly TimeSpan DeletionGracePeriod = TimeSpan.FromDays(30);

    public async Task<AccountDeletionStatusResponse?> GetDeletionStatusAsync(string auth0UserId)
    {
        var user = await _unitOfWork.Users.GetByAuth0UserIdAsync(auth0UserId);
        return user is null ? null : StatusOf(user.DeletionRequestedAtUtc);
    }

    public async Task<AccountDeletionStatusResponse?> RequestDeletionAsync(string auth0UserId)
    {
        var user = await _unitOfWork.Users.GetByAuth0UserIdAsync(auth0UserId);
        if (user is null) return null;

        await RefuseIfTheyStillRunAFamilyAsync(user.Id);

        // Conditional update rather than read-then-save, the same shape the disclosure dismissal
        // uses: whichever request lands first is the one the 30 days are counted from, and a
        // second tap is a no-op that still reports the truth.
        var requestedAt = DateTime.UtcNow;
        await _unitOfWork.Users.TryRequestDeletionAsync(auth0UserId, requestedAt);

        // Re-read, and clear tracking first. Two reasons, and the second is the one that bites:
        // Postgres keeps microseconds where .NET keeps ticks, so the stored timestamp is a few
        // hundred nanoseconds earlier than the one just sent — and the due date a client shows has
        // to be the due date the worker will act on. But `user` above is already tracked with the
        // old value, and `ExecuteUpdateAsync` writes straight past the change tracker, so a plain
        // re-read hands back the stale in-memory entity rather than the row.
        //
        // This also covers the repeated request without a second branch: the conditional update
        // does nothing when one is already outstanding, and the re-read then reports the first
        // request's date, which is exactly what a second tap should be told.
        _unitOfWork.ClearTracking();
        var stored = await _unitOfWork.Users.GetByAuth0UserIdAsync(auth0UserId);

        // Null means the account went between the two reads. Inventing a request for a row that no
        // longer exists would have the caller report a deletion scheduled against nobody.
        if (stored?.DeletionRequestedAtUtc is not { } persisted)
            return null;

        await ReleasePushRegistrationsAsync(stored.Id);

        return StatusOf(persisted);
    }

    /// <summary>
    /// Refuses a deletion request from somebody who is still the admin of a family with other
    /// people in it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Erasing them would leave that family with members and no admin: nobody who can approve a
    /// join request, invite a caregiver, remove one, or move the plan — and the plan is theirs,
    /// so it goes with them. The same rule already governs leaving a family
    /// (<see cref="Exceptions.FamilyRuleException.AdminMustTransferFirst"/>); deleting an account
    /// is leaving every family at once and should not be the way round it.
    /// </para>
    /// <para>
    /// Refused here, at the request, rather than at erasure. The erasure itself runs thirty days
    /// later in <c>RetentionWorker</c>, by which point the person has asked and waited; throwing
    /// there would leave an account that can never be erased, which is a worse failure than the
    /// one this prevents and the wrong kind of answer to give a data-subject request.
    /// </para>
    /// <para>
    /// Somebody alone in their family is not refused — there is nobody to hand it to, and the
    /// family goes with them.
    /// </para>
    /// </remarks>
    private async Task RefuseIfTheyStillRunAFamilyAsync(Guid userId)
    {
        var memberships = (await _unitOfWork.UserOrganizations.GetByUserIdAsync(userId))
            .Where(m => m.IsActive && m.Role == UserRole.Admin)
            .ToList();

        foreach (var membership in memberships)
        {
            var others = (await _unitOfWork.UserOrganizations
                    .GetByOrganizationIdAsync(membership.OrganizationId))
                .Any(m => m.IsActive && m.UserId != userId);

            if (others)
            {
                throw new FamilyRuleException(
                    FamilyRuleException.AdminMustTransferFirst,
                    "You're the admin of a family with other people in it. Hand it to someone else "
                    + "before you delete your account, or they'll be left without anyone who can "
                    + "manage it.");
            }
        }
    }

    /// <summary>
    /// Gives up every push registration this account holds, as part of asking to be deleted.
    /// </summary>
    /// <remarks>
    /// Done here rather than left to the client, because the client cannot be relied on to
    /// manage it: the request can come from a second device, the app's own release is
    /// best-effort and swallows its failures, and after this call
    /// <c>PendingDeletionGateMiddleware</c> refuses the unregister endpoint anyway.
    ///
    /// It matters because a live token stays deliverable. Recipients are resolved on
    /// <c>IsActive</c> and <c>ReceiveAlerts</c> and never on <c>DeletionRequestedAtUtc</c>
    /// (see issue #1144, which is about whether that ought to change), so for the thirty days
    /// the request can still be cancelled this account would go on being pushed to — a monitored
    /// person's health and Safety notifications, on a phone whose owner has asked to be erased
    /// and may well have handed it on.
    ///
    /// Disabled rather than deleted: the 30-day sweep removes them, and cancelling within the
    /// window restores push by itself — the app re-registers on its next launch, and the upsert
    /// keys on (UserId, DeviceId), so the same row comes back live.
    /// </remarks>
    private async Task ReleasePushRegistrationsAsync(Guid userId)
    {
        var live = await _unitOfWork.PushDeviceTokens.FindAsync(
            token => token.UserId == userId && token.DisabledDate == null);

        var released = live.ToList();
        if (released.Count == 0)
            return;

        foreach (var token in released)
        {
            Notifications.DeviceTokenService.Disable(token, "Account deletion requested");
            _unitOfWork.PushDeviceTokens.Update(token);
        }

        await _unitOfWork.SaveChangesAsync();
    }

    public async Task<AccountDeletionStatusResponse?> CancelDeletionAsync(string auth0UserId)
    {
        var user = await _unitOfWork.Users.GetByAuth0UserIdAsync(auth0UserId);
        if (user is null) return null;

        // Past the window, the answer is no. The erasure is due or already running, and letting a
        // cancellation land on day 31 would either revive an account whose data has gone or race
        // the worker for it. The status says so rather than the call silently doing nothing.
        if (user.DeletionRequestedAtUtc is { } requested && !CanStillCancel(requested))
            return StatusOf(requested);

        await _unitOfWork.Users.TryCancelDeletionAsync(auth0UserId);
        return StatusOf(null);
    }

    /// <summary>
    /// Whether a request made at this moment is still inside the window it can be taken back in.
    /// </summary>
    /// <remarks>
    /// The whole window, not part of it: someone who changes their mind on day 29 is exactly who
    /// the grace period is for. Day 31 is not — by then the promise made to them is that the data
    /// is gone.
    /// </remarks>
    private static bool CanStillCancel(DateTime requestedAtUtc) =>
        DateTime.UtcNow < requestedAtUtc + DeletionGracePeriod;

    private static AccountDeletionStatusResponse StatusOf(DateTime? requestedAtUtc) =>
        requestedAtUtc is { } requested
            ? new AccountDeletionStatusResponse(
                true, requested, requested + DeletionGracePeriod, CanStillCancel(requested))
            : new AccountDeletionStatusResponse(false, null, null, false);

    public async Task<bool> DismissHealthDataDisclosureAsync(string auth0UserId)
    {
        var user = await _unitOfWork.Users.GetByAuth0UserIdAsync(auth0UserId);
        if (user == null) return false;

        // Keep the first acknowledgment timestamp — it is the compliance-relevant one. The
        // repository stamps it conditionally in a single statement, so two devices dismissing
        // at the same moment cannot each read "not yet" and both write; whichever lands first
        // is the record and the other is a no-op that still counts as acknowledged.
        if (user.HealthDataDisclosureDismissedDate != null) return true;

        await _unitOfWork.Users.TryRecordHealthDataDisclosureDismissalAsync(auth0UserId, DateTime.UtcNow);
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

        // A guest has no home organization; onboarding status then reports HasOrganization false,
        // which is true — they have members to watch, not a family of their own.
        var organization = user.OrganizationId is { } homeOrganizationId
            ? await _unitOfWork.Organizations.GetByIdAsync(homeOrganizationId)
            : null;
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
