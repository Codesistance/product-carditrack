using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

public interface IPushDeviceTokenRepository : IRepository<PushDeviceToken>
{
    /// <summary>The upsert/lookup key — ciphertext is non-deterministic, so lookups go by fingerprint, never the token itself.</summary>
    Task<PushDeviceToken?> GetByFingerprintAsync(string tokenFingerprint, CancellationToken ct = default);

    Task<PushDeviceToken?> GetByUserAndDeviceAsync(Guid userId, string deviceId, CancellationToken ct = default);

    Task<IReadOnlyList<PushDeviceToken>> GetLiveForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Live tokens not probed for liveness today — the daily silent-push sweep's input set.</summary>
    Task<IReadOnlyList<PushDeviceToken>> GetDueForLivenessProbeAsync(DateTime utcNow, CancellationToken ct = default);

    /// <summary>Disabled 30+ days ago — the hard-delete sweep (§7.2 C2). Tier 1 data is never soft-retained.</summary>
    Task<IReadOnlyList<PushDeviceToken>> GetDueForHardDeleteAsync(DateTime utcNow, CancellationToken ct = default);
}
