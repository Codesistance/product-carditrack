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
    /// Whether an alert escalated to this person — one nobody else answered — may wake them inside
    /// their own quiet hours.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Defaults to false: hold it. That is deliberately the conservative reading, and it is not
    /// the same rule the first caregiver gets. Somebody who added a member chose to be woken about
    /// them, and red and Safety deliveries pierce their quiet hours unconditionally. A second
    /// caregiver is being escalated <em>to</em>, often by a family they joined rather than started,
    /// so they choose.
    /// </para>
    /// <para>
    /// <strong>The cost of the default is real and is the point of the accept-flow question.</strong>
    /// A family where everyone holds has no night cover: an unanswered red alert reaches nobody
    /// until the ladder marks it undelivered. That is why accepting an invitation asks rather than
    /// letting this default decide, and why the caregiver list shows a family whose cover is
    /// missing.
    /// </para>
    /// </remarks>
    public bool EscalatedAlertsPierceQuietHours { get; set; }

    /// <summary>
    /// Opt-in richness (§7.1). Default false — the fail-closed default is content-free, so a
    /// null, unreadable or unmigrated value must resolve to <c>false</c>, never <c>true</c>.
    /// </summary>
    public bool ShowDetailsOnLockScreen { get; set; }

    /// <summary>JSON array of muted <see cref="Enums.DeliveryCategory"/> values. Safety can never appear here — enforced in the service layer, not the schema.</summary>
    public string MutedCategories { get; set; } = "[]";
}
