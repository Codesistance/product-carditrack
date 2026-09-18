using System.Security.Cryptography;
using System.Text;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services.Notifications;

public interface IDeviceTokenService
{
    Task<PushDeviceRegistration> RegisterAsync(
        Guid userId,
        string deviceId,
        DevicePlatform platform,
        string appVersion,
        string rawToken,
        OsAuthorizationStatus osAuthorizationStatus,
        bool safetyChannelEnabled,
        CancellationToken ct = default);

    Task UnregisterAsync(Guid userId, string deviceId, CancellationToken ct = default);
}

/// <summary>
/// The outcome of a registration.
/// </summary>
/// <param name="Token">The caller's row, whether inserted or updated.</param>
/// <param name="DisplacedUserId">
/// Who held this push token before the call, on the occasions where somebody did — see
/// <see cref="DeviceTokenService.RegisterAsync"/>. Null in the ordinary case. Returned rather
/// than logged here because this layer writes no logs; the API records it, and without that
/// record a caregiver losing push to somebody else's install leaves no trace at all.
/// </param>
/// <param name="ReachabilityReconciled">
/// Whether both users' <c>PUSH_UNREACHABLE</c> state was re-evaluated after the claim. False
/// means the displaced caregiver has not been told they are unreachable, and is worth a line in
/// the log — it is not worth failing the registration over, because the claim is already
/// committed and a failed call would cost the record of the displacement itself.
/// </param>
public readonly record struct PushDeviceRegistration(
    PushDeviceToken Token, Guid? DisplacedUserId, bool ReachabilityReconciled);

/// <summary>
/// Upserts a device's push token by fingerprint (§7.2 C2), records OS reachability, and arms
/// <c>PUSH_UNREACHABLE</c> through the existing gap-resolver seam when the OS grant is missing —
/// no new event mechanism, the same direct-service-call pattern <c>INotificationGapResolver</c>
/// already establishes.
/// </summary>
public class DeviceTokenService : IDeviceTokenService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IEncryptionService _encryption;
    private readonly INotificationGapResolver _gapResolver;
    private readonly TimeProvider _timeProvider;

    public DeviceTokenService(
        IUnitOfWork unitOfWork,
        IEncryptionService encryption,
        INotificationGapResolver gapResolver,
        TimeProvider? timeProvider = null)
    {
        _unitOfWork = unitOfWork;
        _encryption = encryption;
        _gapResolver = gapResolver;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Upserts the caller's row for this install, taking the push token off whichever install
    /// held it last.
    /// </summary>
    /// <remarks>
    /// FCM and APNs each issue one token per app installation, which is what the unique index on
    /// <c>TokenFingerprint</c> records. The same token still reaches us under a second install
    /// now and then — a cloned emulator image, an Android device-to-device transfer, a restore
    /// that carried the Firebase installation across — and the upsert key here, (UserId,
    /// DeviceId), does not see that: the lookup misses and the insert used to die on the index,
    /// answering a caregiver's registration with a 500 until the install was wiped.
    ///
    /// Taking the token is the only resolution available. The provider itself delivers a given
    /// token to one install, whichever registered last, so the displaced row could not be
    /// delivered to even if it were kept — and every send attempted against it would put one
    /// caregiver's health notifications on another's screen. The displaced user goes unreachable
    /// instead, which <c>PUSH_UNREACHABLE</c> tells them about, and their install recovers by
    /// itself once the provider issues it a token of its own.
    /// </remarks>
    public async Task<PushDeviceRegistration> RegisterAsync(
        Guid userId,
        string deviceId,
        DevicePlatform platform,
        string appVersion,
        string rawToken,
        OsAuthorizationStatus osAuthorizationStatus,
        bool safetyChannelEnabled,
        CancellationToken ct = default)
    {
        var fingerprint = Fingerprint(rawToken);

        PushDeviceRegistration registration;
        try
        {
            registration = await ClaimAsync(
                userId, deviceId, platform, appVersion, rawToken, fingerprint,
                osAuthorizationStatus, safetyChannelEnabled, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Tracking is dropped first: the failed entries would otherwise fail every later save
            // on this scope.
            _unitOfWork.ClearTracking();

            // Only a lost race earns a second pass. Two installs registering the same token at
            // once both read no holder, both insert, and the unique index fails the loser — by
            // the time we are here the winner has committed, so somebody now holds the
            // fingerprint and the retry is the ordinary claim path. Nothing else is: a dead
            // database, an encryption fault or a cancelled request would only fail again, more
            // slowly, and a fault that should surface immediately would be hidden behind a
            // second transaction's worth of latency. This layer cannot name the provider's
            // 23505 by type — it references no database library, deliberately — so the evidence
            // of the race is read from the data, the same shape OnboardingService uses for the
            // unique index on Auth0UserId.
            if (!await AnotherInstallHoldsTokenAsync(fingerprint, ct))
                throw;

            registration = await ClaimAsync(
                userId, deviceId, platform, appVersion, rawToken, fingerprint,
                osAuthorizationStatus, safetyChannelEnabled, ct);
        }

        // Everything below is after the claim has committed, and none of it may fail the call.
        //
        // The displaced caregiver's picture changed as much as the caller's did — they just lost
        // a device — and re-evaluating theirs is what arms PUSH_UNREACHABLE for them. But a throw
        // here would answer a registration that *did* succeed with a 500, and the client's retry
        // would then find the caller already holding the token: DisplacedUserId comes back null,
        // and the one record that a caregiver lost push is gone for good. The reconciliation is
        // recoverable — their own next inbox read resolves their gaps (NotificationService does
        // it for the user reading them) — and the record is not, so the record wins.
        //
        // Reported rather than swallowed: the API logs the reassignment either way, and says so
        // when this part did not finish.
        var reconciled = await TryReconcileAsync(userId, registration.DisplacedUserId, ct);

        return registration with { ReachabilityReconciled = reconciled };
    }

    /// <summary>
    /// Re-evaluates <c>PUSH_UNREACHABLE</c> for the caller and, when the token was taken from
    /// somebody, for them too — the displaced user first, since the caller has a client that
    /// retries a failed registration where they have nothing that would notice.
    /// </summary>
    /// <returns>False if any of it did not complete, including on cancellation.</returns>
    private async Task<bool> TryReconcileAsync(Guid userId, Guid? displacedUserId, CancellationToken ct)
    {
        try
        {
            if (displacedUserId is { } displaced && displaced != userId)
                await ReconcileReachabilityAsync(displaced, ct);

            await ReconcileReachabilityAsync(userId, ct);
            return true;
        }
        catch
        {
            // Cancellation included, and deliberately: the caller having gone is no reason to
            // lose the displacement record, which is written from the value this returns into.
            return false;
        }
    }

    /// <summary>
    /// Whether the fingerprint is held by a row now — the evidence that a failed save was a lost
    /// race rather than a fault. A probe that itself fails answers "no", so the original failure
    /// is the one that surfaces rather than being masked by this one.
    /// </summary>
    private async Task<bool> AnotherInstallHoldsTokenAsync(string fingerprint, CancellationToken ct)
    {
        try
        {
            return await _unitOfWork.PushDeviceTokens.GetByFingerprintAsync(fingerprint, ct) is not null;
        }
        catch
        {
            return false;
        }
    }

    private async Task<PushDeviceRegistration> ClaimAsync(
        Guid userId,
        string deviceId,
        DevicePlatform platform,
        string appVersion,
        string rawToken,
        string fingerprint,
        OsAuthorizationStatus osAuthorizationStatus,
        bool safetyChannelEnabled,
        CancellationToken ct)
    {
        await _unitOfWork.BeginTransactionAsync();
        try
        {
            // Upsert by (UserId, DeviceId) — a token rotation on the same install must not read
            // as a new device — but re-encrypt every call, since the raw token itself may have
            // rotated.
            var existing = await _unitOfWork.PushDeviceTokens.GetByUserAndDeviceAsync(userId, deviceId, ct);

            Guid? displacedUserId = null;
            var holder = await _unitOfWork.PushDeviceTokens.GetByFingerprintAsync(fingerprint, ct);
            if (holder is not null && holder.Id != existing?.Id)
            {
                displacedUserId = holder.UserId;

                // Deleted rather than disabled: a token moving to another install is not a
                // delivery failure worth a DisabledReason, and a disabled row would hold the
                // index against every future registration until the 30-day sweep cleared it —
                // the same 500, just later. Keeping another user's encrypted token also has no
                // purpose left under the Tier 1 rule in data_protection_architecture.md.
                _unitOfWork.PushDeviceTokens.Remove(holder);

                // Freed in its own save: the write below takes the fingerprint this one
                // releases, and only statement order inside the transaction guarantees the
                // unique index sees the release first.
                await _unitOfWork.SaveChangesAsync();
            }

            var entity = existing ?? new PushDeviceToken { UserId = userId, DeviceId = deviceId };
            entity.Platform = platform;
            entity.AppVersion = appVersion;
            entity.Token = _encryption.Encrypt(rawToken);
            entity.TokenFingerprint = fingerprint;
            entity.OsAuthorizationStatus = osAuthorizationStatus;
            entity.SafetyChannelEnabled = safetyChannelEnabled;
            entity.LastSeenDate = UtcNow;
            entity.DisabledDate = null;
            entity.DisabledReason = null;

            if (existing is null)
                await _unitOfWork.PushDeviceTokens.AddAsync(entity);
            else
                _unitOfWork.PushDeviceTokens.Update(entity);

            await _unitOfWork.SaveChangesAsync();
            await _unitOfWork.CommitTransactionAsync();

            // Reconciliation is the caller's to run, once, after both attempts have settled.
            return new PushDeviceRegistration(entity, displacedUserId, ReachabilityReconciled: false);
        }
        catch
        {
            await _unitOfWork.RollbackTransactionAsync();
            throw;
        }
    }

    public async Task UnregisterAsync(Guid userId, string deviceId, CancellationToken ct = default)
    {
        var token = await _unitOfWork.PushDeviceTokens.GetByUserAndDeviceAsync(userId, deviceId, ct);
        if (token is null)
            return;

        Disable(token, "Unregistered by client");
        _unitOfWork.PushDeviceTokens.Update(token);
        await _unitOfWork.SaveChangesAsync();

        await ReconcileReachabilityAsync(userId, ct);
    }

    /// <summary>
    /// Disables a token from a <c>Permanent</c> send outcome (FCM <c>UNREGISTERED</c> / APNs
    /// <c>410</c>) or a 7-day liveness miss — called by the dispatch worker, not exposed to the API.
    /// </summary>
    internal static void Disable(PushDeviceToken token, string reason)
    {
        token.DisabledDate = DateTime.UtcNow;
        token.DisabledReason = reason;
    }

    /// <summary>
    /// Re-evaluates <c>PUSH_UNREACHABLE</c> for the user whenever their reachability picture
    /// changes — registration, unregistration, or a permanent send failure. The rule itself
    /// (<see cref="Rules.PushUnreachableRule"/>) reads live token state, so this is just the
    /// existing gap-resolver trigger, not new detection logic.
    /// </summary>
    private async Task ReconcileReachabilityAsync(Guid userId, CancellationToken ct) =>
        await _gapResolver.ResolveForUserAsync(userId, ct);

    private static string Fingerprint(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
}
