using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Interfaces.Repositories;

public interface IPushDeviceTokenRepository : IRepository<PushDeviceToken>
{
    /// <summary>The upsert/lookup key — ciphertext is non-deterministic, so lookups go by fingerprint, never the token itself.</summary>
    Task<PushDeviceToken?> GetByFingerprintAsync(string tokenFingerprint, CancellationToken ct = default);

    Task<PushDeviceToken?> GetByUserAndDeviceAsync(Guid userId, string deviceId, CancellationToken ct = default);

    /// <summary>
    /// Not-disabled tokens actually reachable for a send of <paramref name="category"/> — OS
    /// authorization must be Granted or Provisional (a Denied token can never deliver, so
    /// attempting it just wastes a send and risks misclassifying "muted" as "retryable"), and for
    /// <see cref="DeliveryCategory.Safety"/> specifically, the OS-level Safety channel must also
    /// be on. <c>SafetyChannelEnabled</c> gates only that one channel — a caregiver who muted
    /// Safety at the OS level can still receive Health/Nudge pushes on their other channels, so
    /// this must not blanket-filter every category by it.
    /// </summary>
    Task<IReadOnlyList<PushDeviceToken>> GetLiveForUserAsync(
        Guid userId, DeliveryCategory category, CancellationToken ct = default);

    /// <summary>Live tokens not probed for liveness today — the daily silent-push sweep's input set.</summary>
    Task<IReadOnlyList<PushDeviceToken>> GetDueForLivenessProbeAsync(DateTime utcNow, CancellationToken ct = default);

    /// <summary>Disabled 30+ days ago — the hard-delete sweep (§7.2 C2). Tier 1 data is never soft-retained.</summary>
    Task<IReadOnlyList<PushDeviceToken>> GetDueForHardDeleteAsync(DateTime utcNow, CancellationToken ct = default);
}
