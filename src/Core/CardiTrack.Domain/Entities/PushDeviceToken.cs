using CardiTrack.Domain.Common;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// One installed app instance's FCM registration token. Tier 1 data
/// (data_protection_architecture.md §2, HIPAA Safe Harbor category 13) — a stable
/// cross-reinstall device identifier, encrypted at rest the same way
/// <c>DeviceConnection</c>'s OAuth tokens are (notification_engine.md §7.2 C2).
/// </summary>
/// <remarks>
/// Disabled tokens are hard-deleted at 30 days, not soft-retained — see the retention sweep in
/// <c>DataRetentionWorker</c>. Enumerated in the Safe Harbor export exclusion and the erasure
/// sweep alongside every other Tier 1 table.
/// </remarks>
public class PushDeviceToken : BaseEntity
{
    public Guid UserId { get; set; }

    /// <summary>Stable per install, distinct from the token itself so a token rotation doesn't read as a new device.</summary>
    public string DeviceId { get; set; } = string.Empty;

    public DevicePlatform Platform { get; set; }
    public string AppVersion { get; set; } = string.Empty;

    /// <summary>AES-256-GCM via <c>IEncryptionService</c>, same pattern as <c>DeviceConnection</c>'s OAuth tokens — never stored in the clear.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>SHA-256 hex of the plaintext token — the upsert/lookup key, since GCM ciphertext is non-deterministic.</summary>
    public string TokenFingerprint { get; set; } = string.Empty;

    public OsAuthorizationStatus OsAuthorizationStatus { get; set; } = OsAuthorizationStatus.NotDetermined;

    /// <summary>Whether the OS-level Safety notification channel is enabled — muting it at the OS level arms <c>PUSH_UNREACHABLE</c> the same as a revoked permission.</summary>
    public bool SafetyChannelEnabled { get; set; } = true;

    public DateTime LastSeenDate { get; set; }

    /// <summary>Last time this token acknowledged a delivery — the token-liveness signal. Silent 7 days and it is presumed dead.</summary>
    public DateTime? LastAckDate { get; set; }

    public DateTime? DisabledDate { get; set; }
    public string? DisabledReason { get; set; }

    public PushDeviceToken()
    {
        LastSeenDate = DateTime.UtcNow;
    }

    public bool IsLive(DateTime utcNow) =>
        DisabledDate is null && (LastAckDate is null || LastAckDate.Value >= utcNow.AddDays(-7));
}
