using CardiTrack.Domain.Common;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// Per-user push preferences — quiet hours, lock-screen detail, muted categories
/// (notification_engine.md §8, §11). One row per user; created lazily on first read with
/// safe defaults, since a null/unmigrated row must fail closed (§7.1) rather than block on a
/// provisioning step.
/// </summary>
public class NotificationPreference : BaseEntity
{
    public Guid UserId { get; set; }

    public TimeOnly? QuietHoursStart { get; set; }
    public TimeOnly? QuietHoursEnd { get; set; }

    /// <summary>
    /// Opt-in richness (§7.1). Default false — the fail-closed default is content-free, so a
    /// null, unreadable or unmigrated value must resolve to <c>false</c>, never <c>true</c>.
    /// </summary>
    public bool ShowDetailsOnLockScreen { get; set; }

    /// <summary>JSON array of muted <see cref="Enums.DeliveryCategory"/> values. Safety can never appear here — enforced in the service layer, not the schema.</summary>
    public string MutedCategories { get; set; } = "[]";
}
