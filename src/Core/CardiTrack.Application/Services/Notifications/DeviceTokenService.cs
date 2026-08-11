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
    Task<PushDeviceToken> RegisterAsync(
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

    public async Task<PushDeviceToken> RegisterAsync(
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

        // Upsert by (UserId, DeviceId) — a token rotation on the same install must not read as a
        // new device — but re-encrypt every call, since the raw token itself may have rotated.
        var existing = await _unitOfWork.PushDeviceTokens.GetByUserAndDeviceAsync(userId, deviceId, ct);

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

        await ReconcileReachabilityAsync(userId, ct);

        return entity;
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
