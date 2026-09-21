namespace CardiTrack.Application.Services;

/// <summary>
/// How long a member's Advise observation log is kept.
/// </summary>
/// <remarks>
/// <para>
/// A constant rather than a worker option, following <see cref="UserService.DeletionGracePeriod"/>
/// and for the same reason: two places read this — the sweep that deletes, and the API that clamps
/// a requested window and then tells the caller what it actually served — and a configuration
/// value would let them disagree. The visible failure is the mild one, an API promising a year of
/// record that the sweep took at ninety days; the quiet one is worse, a report rendering a window
/// that silently has a hole in it.
/// </para>
/// <para>
/// A year because the record's purpose is a routine review appointment, and the interval between
/// those is the span a caregiver is being asked to account for. Long enough to cover the one
/// before last; short enough that derived health prose about a person does not accumulate
/// indefinitely for a purpose nobody has.
/// </para>
/// </remarks>
public static class AdviseObservationRetention
{
    public static readonly TimeSpan Period = TimeSpan.FromDays(365);

    /// <summary>
    /// The oldest entry still retained at <paramref name="utcNow"/>. Anything older has either
    /// gone already or goes on the next sweep.
    /// </summary>
    public static DateTime CutoffAt(DateTime utcNow) => utcNow - Period;

    /// <summary>
    /// The most rows one sweep will remove — the same bounded-batch shape the other retention
    /// passes use, so a sweep that has fallen behind catches up over several ticks rather than
    /// loading an unbounded set into memory in one.
    /// </summary>
    public const int SweepBatchSize = 500;
}
