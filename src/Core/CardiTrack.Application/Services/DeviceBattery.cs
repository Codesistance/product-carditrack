using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <summary>
/// How severe a low reading is — Critical outranks Urgent outranks Warning, matching the order a
/// caregiver would act in. Ordered so a higher tier compares greater than a lower one.
/// </summary>
public enum BatteryAlertTier
{
    Warning = 1,
    Urgent = 2,
    Critical = 3,
}

/// <summary>
/// The thresholds that decide when a wearable's battery is worth showing and worth warning about.
/// Shared so the device list and the low-battery rule cannot drift into disagreeing about what
/// "low" means — a tile reading a comfortable 15% beside a notification calling it critical is
/// the kind of contradiction that costs a caregiver their trust in both.
/// </summary>
public static class DeviceBattery
{
    /// <summary>
    /// At or below this percentage the battery is worth a first word — still enough runway to
    /// plan around, but close enough that "charge it tonight" beats finding out tomorrow that
    /// nobody did. <see cref="BatteryAlertTier.Warning"/>.
    /// </summary>
    public const int WarningThresholdPercent = 30;

    /// <summary>
    /// At or below this percentage the runway is short enough to actively worry about.
    /// <see cref="BatteryAlertTier.Urgent"/>.
    /// </summary>
    public const int UrgentThresholdPercent = 20;

    /// <summary>
    /// At or below this percentage the wearable is close enough to stopping that a caregiver
    /// should be told right away. <see cref="BatteryAlertTier.Critical"/>.
    /// </summary>
    public const int CriticalThresholdPercent = 10;

    /// <summary>The provider band meaning the device is flat and has already stopped reporting.</summary>
    public const string EmptyStatus = "Empty";

    /// <summary>The provider band meaning the device is close to flat.</summary>
    public const string LowStatus = "Low";

    /// <summary>
    /// How long a battery reading stays presentable. Connections are pulled every ten minutes, so
    /// a reading this old means syncing itself has stopped — at which point the number says
    /// nothing about the battery now, and the device list's own stale-sync signalling is the
    /// honest thing to show instead of a percentage frozen half a day ago. Tightened from 24h: a
    /// reading a caregiver is meant to act on tonight should not still be standing in for the
    /// battery's state most of a day later.
    /// </summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromHours(12);

    /// <summary>Whether a reading captured at <paramref name="readAtUtc"/> may still be shown.</summary>
    public static bool IsFresh(DateTime? readAtUtc, DateTime utcNow) =>
        readAtUtc is { } read && utcNow - read < FreshFor;

    /// <summary>
    /// The severity tier a reading falls into, or null when the battery isn't worth mentioning.
    /// </summary>
    /// <remarks>
    /// A percentage is read first when one exists — it is the more precise signal. <c>Empty</c>
    /// means the device has already stopped, which is Critical by definition regardless of
    /// whether a stale percentage came with it. A bare <c>Low</c> band with no percentage is read
    /// as Urgent: not as dire as a device that has actually gone flat, but with no number to place
    /// it more precisely than that, Urgent is the honest middle ground rather than a guess in
    /// either direction.
    /// </remarks>
    public static BatteryAlertTier? GetTier(int? level, string? status)
    {
        if (string.Equals(status, EmptyStatus, StringComparison.OrdinalIgnoreCase))
            return BatteryAlertTier.Critical;

        if (level is { } percent)
        {
            if (percent <= CriticalThresholdPercent)
                return BatteryAlertTier.Critical;
            if (percent <= UrgentThresholdPercent)
                return BatteryAlertTier.Urgent;
            if (percent <= WarningThresholdPercent)
                return BatteryAlertTier.Warning;
            return null;
        }

        return string.Equals(status, LowStatus, StringComparison.OrdinalIgnoreCase)
            ? BatteryAlertTier.Urgent
            : null;
    }

    /// <summary>Whether a reading warrants warning a caregiver at all, regardless of tier.</summary>
    public static bool IsLow(int? level, string? status) => GetTier(level, status) is not null;

    /// <summary>
    /// The <see cref="NotificationPriority"/> a tier maps to, for the inbox and dashboard card
    /// ordering — delivery itself (push, critical flag, quiet-hours override) is unaffected by
    /// tier: every reading low enough to be worth a nudge is worth saying right away, per
    /// <see cref="Notifications.Rules.DeviceBatteryLowRule"/>'s own reasoning. High stands in for
    /// "Urgent" — <see cref="NotificationPriority"/> has no literal Urgent value, and is shared
    /// across every rule in the catalogue rather than owned by this one.
    /// </summary>
    public static NotificationPriority PriorityFor(BatteryAlertTier tier) => tier switch
    {
        BatteryAlertTier.Critical => NotificationPriority.Critical,
        BatteryAlertTier.Urgent => NotificationPriority.High,
        BatteryAlertTier.Warning => NotificationPriority.Medium,
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, null),
    };
}
