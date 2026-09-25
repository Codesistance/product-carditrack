using System.ComponentModel.DataAnnotations;

namespace CardiTrack.Domain.Enums;

/// <summary>
/// What is known about the night that ended on an <see cref="Entities.ActivityLog"/>'s day.
/// </summary>
/// <remarks>
/// <para>
/// A null <see cref="Entities.ActivityLog.SleepMinutes"/> used to say everything and therefore
/// nothing: the watch was off, the night had not synced yet, or the wearer never slept — three
/// facts a caregiver would act on differently, read as one. The status tells them apart, and the
/// rule that sets it is <c>NightSleepStatus</c> in Application (decision 2026-09-25).
/// </para>
/// <para>
/// Stored by name, like the other enums on this table, so a reordering can never quietly relabel
/// a night that was already recorded.
/// </para>
/// </remarks>
public enum NightSleepStatus
{
    /// <summary>A sleep session arrived for the night.</summary>
    [Display(Name = "Slept")]
    Slept = 1,

    /// <summary>
    /// No sleep session arrived, and the heart rate shows the watch was worn through the night and
    /// has synced since the member's usual waking time. Counted as a night of no sleep — 0 minutes.
    /// </summary>
    [Display(Name = "Awake")]
    Awake = 2,

    /// <summary>
    /// No sleep session and no sign the watch was worn overnight, though it has synced since the
    /// member's usual waking time. Nothing reached us for this night.
    /// </summary>
    [Display(Name = "No data")]
    NoData = 3,

    /// <summary>
    /// No sleep session yet, and nothing has synced since the member's usual waking time — the
    /// night may still arrive.
    /// </summary>
    [Display(Name = "Pending")]
    Pending = 4,
}
