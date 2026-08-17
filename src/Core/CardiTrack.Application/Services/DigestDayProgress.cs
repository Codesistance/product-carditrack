using System.Globalization;
using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Services;

/// <summary>
/// How far into their own day the member is, and therefore how much of a running daily total the
/// readings can honestly account for.
/// </summary>
/// <remarks>
/// <para>
/// Steps and active minutes accumulate: a figure for a day still in progress is a fraction of what
/// that day will end on, and the fraction is the whole story. The prompts already labelled today's
/// row "still in progress — activity totals are partial", which is true at 07:00 and true at 23:00
/// and says nothing that distinguishes them. Handed a partial total, a usual-pattern yardstick and
/// no clock, a model reads a just-woken member's step count as a collapse in activity — which is
/// exactly what a caregiver saw at 07:14 one morning, alongside a summary that had already been
/// regenerated a dozen times since midnight to say it.
/// </para>
/// <para>
/// So the clock becomes a computed value like every other number in these prompts (the standing
/// division of labour in docs/llm_design.md: .NET computes, the model only phrases), and the same
/// value gates regeneration. Those two uses are deliberately the same object — a summary the
/// pipeline will not pay for and a sentence the model must not write both come down to "there is
/// not enough of this day yet", and stating that twice is how the two drift apart.
/// </para>
/// <para>
/// Pure, free of I/O, taking the member's already-resolved local time so every threshold is
/// unit-testable to its boundary — the same stance as <see cref="StatisticalAlertRules"/> and
/// <see cref="DigestRefreshRules"/>.
/// </para>
/// </remarks>
public sealed class DigestDayProgress
{
    /// <summary>
    /// Wake time assumed for a member whose baseline has not established one — either because they
    /// are still being learned or because their device reports no sleep. Deliberately early: this
    /// number decides when the pipeline is willing to start describing a day, and guessing late
    /// would hold back a summary for a member who really is up and about.
    /// </summary>
    public static readonly TimeOnly DefaultWakeTime = new(07, 00);

    /// <summary>
    /// Bedtime assumed when the baseline has not established one. The far end of the waking day,
    /// used only to turn "hours since waking" into a fraction of the day — an hour out either way
    /// moves the fraction by a few percent and changes no threshold here.
    /// </summary>
    public static readonly TimeOnly DefaultBedtime = new(22, 00);

    /// <summary>
    /// Shortest waking day this will reason about, in hours. Guards the fraction below against a
    /// baseline that has somehow recorded a bedtime an hour after the wake time, which would make
    /// every reading past mid-morning "a full day" and hand back the bug this class exists to fix.
    /// </summary>
    private const double MinimumWakingHours = 8;

    /// <summary>
    /// How long after waking the day still counts as <see cref="IsEarlyInTheDay"/>. Someone who has
    /// been up three hours has had breakfast and possibly a walk; before that the readings are a
    /// morning, and a running total is mostly the fact that the day is young.
    /// </summary>
    public const double EarlyDayHours = 3;

    private DigestDayProgress(
        DateTime localNow, TimeOnly wakeTime, TimeOnly bedtime, double hoursSinceWake, double wakingDayElapsed)
    {
        LocalNow = localNow;
        WakeTime = wakeTime;
        Bedtime = bedtime;
        HoursSinceWake = hoursSinceWake;
        WakingDayElapsed = wakingDayElapsed;
    }

    /// <summary>The member's own wall clock.</summary>
    public DateTime LocalNow { get; }

    /// <summary>Their typical wake time, or <see cref="DefaultWakeTime"/> when none is established.</summary>
    public TimeOnly WakeTime { get; }

    /// <summary>Their typical bedtime, or <see cref="DefaultBedtime"/> when none is established.</summary>
    public TimeOnly Bedtime { get; }

    /// <summary>
    /// Hours since they usually wake, negative before it. Not clamped: the sign is what
    /// <see cref="IsBeforeWake"/> reads, and how far before matters when the digest decides whether
    /// there is a day here at all.
    /// </summary>
    public double HoursSinceWake { get; }

    /// <summary>
    /// How much of their waking day has passed, from 0 at their wake time to 1 at their bedtime.
    /// This is the number a running total should be read against — 26 steps is a collapse against a
    /// whole day and unremarkable against the 3% of one that has actually happened.
    /// </summary>
    public double WakingDayElapsed { get; }

    /// <summary>
    /// The member has not reached their usual wake time. Nothing of today has happened yet, so
    /// there is nothing about today to summarise and no running total worth an opinion.
    /// </summary>
    public bool IsBeforeWake => HoursSinceWake <= 0;

    /// <summary>
    /// Up, but not long enough for a running total to mean much — see <see cref="EarlyDayHours"/>.
    /// True before waking too: a day that has not started is not a day that is well underway.
    /// </summary>
    public bool IsEarlyInTheDay => HoursSinceWake < EarlyDayHours;

    /// <summary>
    /// Where the member is in their own day, given their local clock and — when their baseline has
    /// established one — their own waking hours.
    /// </summary>
    /// <param name="localNow">
    /// The member's local time, already resolved through <see cref="MemberAnchorTimeZone"/> by the
    /// caller. Taken rather than fetched so this stays pure and so the caller cannot end up
    /// reasoning about one clock while the prompt beside it quotes another.
    /// </param>
    /// <param name="baseline">
    /// The established baseline, for the member's own waking hours. Null while they are still being
    /// learned, which falls back to <see cref="DefaultWakeTime"/> and <see cref="DefaultBedtime"/>.
    /// </param>
    /// <param name="timeZone">
    /// The member's anchor zone. Baseline wake/bed times are stored as UTC clock faces
    /// (<see cref="BaselineCalculator"/>); without the zone they cannot be compared to
    /// <paramref name="localNow"/>. Null keeps the defaults (and any already-local test fixtures)
    /// as wall-clock values.
    /// </param>
    public static DigestDayProgress For(
        DateTime localNow, PatternBaseline? baseline, TimeZoneInfo? timeZone = null)
    {
        var wake = LocalClock(baseline?.TypicalWakeTime, localNow, timeZone) ?? DefaultWakeTime;
        var bed = LocalClock(baseline?.TypicalBedtime, localNow, timeZone) ?? DefaultBedtime;

        var wakingHours = (bed.ToTimeSpan() - wake.ToTimeSpan()).TotalHours;
        // A bedtime past midnight comes back negative against the same civil day; a bedtime the
        // baseline has recorded implausibly close to the wake time is guarded by the same floor.
        if (wakingHours <= 0)
            wakingHours += 24;
        wakingHours = Math.Max(wakingHours, MinimumWakingHours);

        var sinceWake = (localNow.TimeOfDay - wake.ToTimeSpan()).TotalHours;
        var elapsed = Math.Clamp(sinceWake / wakingHours, 0, 1);

        return new DigestDayProgress(localNow, wake, bed, sinceWake, elapsed);
    }

    /// <summary>
    /// The baseline's UTC time-of-day, as the member's local wall clock on the day
    /// <paramref name="localNow"/> falls in. Null in stays null; without a zone the stored face
    /// is returned unchanged so unit fixtures that already speak in local hours keep working.
    /// </summary>
    private static TimeOnly? LocalClock(TimeOnly? utcTimeOfDay, DateTime localNow, TimeZoneInfo? timeZone)
    {
        if (utcTimeOfDay is not { } utcClock)
            return null;
        if (timeZone is null)
            return utcClock;

        var utcNow = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(localNow, DateTimeKind.Unspecified), timeZone);
        var utcInstant = DateTime.SpecifyKind(
            utcNow.Date.Add(utcClock.ToTimeSpan()), DateTimeKind.Utc);
        return TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcInstant, timeZone));
    }

    /// <summary>
    /// The clock, as a prompt phrase — what time it is for this member, how long they have been up,
    /// and how much of a running daily total the day can possibly account for yet.
    /// </summary>
    /// <remarks>
    /// Written as prose rather than as fields because it is read inline in the day label it
    /// qualifies, and because a bare percentage is a figure a model will quote back at a caregiver.
    /// Invariant culture throughout: the prompt is model input and a cacheable fixed-prefix
    /// construction, so nothing in it may vary with the host's ambient culture.
    /// </remarks>
    public string Describe()
    {
        var clock = LocalNow.ToString("HH:mm", CultureInfo.InvariantCulture);

        if (IsBeforeWake)
        {
            return $"{clock} local, before their usual waking time of "
                + $"{WakeTime.ToString("HH\\:mm", CultureInfo.InvariantCulture)} — today has barely begun";
        }

        var hours = HoursSinceWake.ToString("0.#", CultureInfo.InvariantCulture);
        var percent = (WakingDayElapsed * 100).ToString("0", CultureInfo.InvariantCulture);
        var wake = WakeTime.ToString("HH\\:mm", CultureInfo.InvariantCulture);

        return $"{clock} local, about {hours} hours since their usual waking time of {wake} "
            + $"— roughly {percent}% of their waking day has passed, so today's running totals "
            + "cover only that much of it and will keep rising";
    }
}
