using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <summary>
/// Decides what is known about a night that brought no sleep session: the wearer was awake with
/// the watch on, nothing reached us, or it may still arrive.
/// </summary>
/// <remarks>
/// <para>
/// Decision 2026-09-25, stated by the product owner as "no ambiguity": a night the heart rate
/// shows the watch was worn through, with no sleep recorded, is a night the member was awake —
/// counted as 0 hours everywhere nights are counted. No sleep, no other data and nothing synced
/// since is no data. Until this existed, all three were one null, and every surface said the same
/// thing about a sleepless night as about a watch left on the side.
/// </para>
/// <para>
/// The night window is the member's own: their typical bedtime the evening before to their typical
/// waking time that morning, from the baseline, or 22:00 to 07:00 on their clock until those are
/// learned. "Worn" is heart rate on at least <see cref="WornCoverage"/> of that window's minutes —
/// the same bar the real-time assessment sets before it will read an hour at all.
/// </para>
/// <para>
/// Awake is declared only once something has synced from after the usual waking time. The heart
/// rate for a night can land before the provider has finished the night's sleep session, and a
/// status that called that gap "awake" would be the ambiguity this exists to remove; until the
/// post-wake sync, a worn night is <see cref="NightSleepStatus.Pending"/>.
/// </para>
/// <para>
/// Pure: the aggregation recompute fetches the heart rate and the baseline and hands them in, so
/// every branch here is testable without a host.
/// </para>
/// </remarks>
public static class NightSleepClassifier
{
    /// <summary>The share of the night window's minutes heart rate must cover for the watch to count as worn.</summary>
    public const decimal WornCoverage = 0.75m;

    /// <summary>The member's bedtime, on their own clock, until the baseline has learned one.</summary>
    public static readonly TimeOnly DefaultBedtime = new(22, 0);

    /// <summary>The member's waking time, on their own clock, until the baseline has learned one.</summary>
    public static readonly TimeOnly DefaultWakeTime = new(7, 0);

    /// <summary>
    /// The night that ended on <paramref name="date"/> (the member's civil day), as UTC instants.
    /// </summary>
    /// <remarks>
    /// The baseline stores its bed and wake times as UTC clock faces
    /// (<see cref="BaselineCalculator"/>), read here on the member's clock via
    /// <see cref="BaselineClock"/>. A bedtime before noon is one past midnight — the same night,
    /// already on <paramref name="date"/>. A learned pair that would make the window empty or
    /// backwards falls back to the defaults rather than classify against nothing.
    /// </remarks>
    public static (DateTime FromUtc, DateTime ToUtc) NightWindowUtc(
        DateOnly date, PatternBaseline? baseline, TimeZoneInfo timeZone)
    {
        // BaselineClock anchors a face to a UTC date: the UTC dates the default bed and wake times
        // fall on are close enough to pick the right side of a daylight-saving change, where the
        // member's civil date can be a day off.
        var defaults = Window(date, DefaultBedtime, DefaultWakeTime, timeZone);
        var window = Window(
            date,
            BaselineClock.Local(baseline?.TypicalBedtime, DateOnly.FromDateTime(defaults.FromUtc), timeZone) ?? DefaultBedtime,
            BaselineClock.Local(baseline?.TypicalWakeTime, DateOnly.FromDateTime(defaults.ToUtc), timeZone) ?? DefaultWakeTime,
            timeZone);

        return window.FromUtc < window.ToUtc ? window : defaults;
    }

    private static (DateTime FromUtc, DateTime ToUtc) Window(
        DateOnly date, TimeOnly bedtime, TimeOnly wakeTime, TimeZoneInfo timeZone)
    {
        var bedDate = bedtime.Hour >= 12 ? date.AddDays(-1) : date;
        return (
            ToUtc(bedDate.ToDateTime(bedtime), timeZone),
            ToUtc(date.ToDateTime(wakeTime), timeZone));
    }

    private static DateTime ToUtc(DateTime local, TimeZoneInfo timeZone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        // The hour a spring-forward skips does not exist on the member's clock; an hour later is
        // the moment that clock actually showed next.
        if (timeZone.IsInvalidTime(unspecified))
            unspecified = unspecified.AddHours(1);

        return TimeZoneInfo.ConvertTimeToUtc(unspecified, timeZone);
    }

    /// <summary>
    /// The status of a night, from what arrived for it.
    /// </summary>
    /// <param name="sleepMinutes">The night's sleep as the provider reported it, or null when no session arrived.</param>
    /// <param name="wornMinutes">Minutes of the night window heart rate covers.</param>
    /// <param name="windowMinutes">The night window's length in minutes.</param>
    /// <param name="syncedSinceWake">Whether any heart rate has arrived from after the window's end.</param>
    /// <param name="windowOver">Whether the window's end has passed at all.</param>
    public static NightSleepStatus Classify(
        int? sleepMinutes, int wornMinutes, int windowMinutes, bool syncedSinceWake, bool windowOver)
    {
        if (sleepMinutes is not null)
            return NightSleepStatus.Slept;

        // A night not over yet is a night nothing can be said about.
        if (!windowOver)
            return NightSleepStatus.Pending;

        var worn = windowMinutes > 0 && wornMinutes >= windowMinutes * WornCoverage;
        return (worn, syncedSinceWake) switch
        {
            (true, true) => NightSleepStatus.Awake,
            (true, false) => NightSleepStatus.Pending,
            _ => NightSleepStatus.NoData,
        };
    }

    /// <summary>
    /// Counts heart rate over a night window: the minutes of the window that carry a sample, and
    /// whether any sample falls after it.
    /// </summary>
    /// <param name="heartRate">The member's merged heart-rate minutes, slot 0 being <paramref name="seriesFromUtc"/>.</param>
    public static (int WornMinutes, bool SyncedSinceWake) Read(
        IReadOnlyList<float?> heartRate, DateTime seriesFromUtc, (DateTime FromUtc, DateTime ToUtc) window)
    {
        var first = Math.Max(0, (int)(window.FromUtc - seriesFromUtc).TotalMinutes);
        var end = Math.Min(heartRate.Count, (int)(window.ToUtc - seriesFromUtc).TotalMinutes);

        var worn = 0;
        for (var minute = first; minute < end; minute++)
        {
            if (heartRate[minute].HasValue)
                worn++;
        }

        var synced = false;
        for (var minute = Math.Max(0, end); minute < heartRate.Count && !synced; minute++)
            synced = heartRate[minute].HasValue;

        return (worn, synced);
    }
}
