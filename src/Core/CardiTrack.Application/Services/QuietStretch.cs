namespace CardiTrack.Application.Services;

/// <summary>
/// How long a CardiMember has gone without anything being raised about them, and whether that
/// silence has earned the right to be reported back as good news.
/// </summary>
/// <remarks>
/// <para>
/// The whole product is built to speak up when something is wrong, which leaves a family reading
/// nothing at all for weeks and no way to tell "we looked and they are fine" apart from "nobody is
/// looking". This type is the difference between the two, and it is deliberately strict: silence
/// only counts once every part of the pipeline that would have spoken up was actually running.
/// A member who is paused, whose device stopped syncing, or who has no established baseline yet
/// produces no alerts either — and calling that "all is well" would be the single most damaging
/// thing this app could say.
/// </para>
/// <para>
/// Pure over <see cref="QuietStretchContext"/>, no clock call and no I/O, the same shape
/// <see cref="Notifications.DeliveryPlanner"/> keeps — the dashboard read and the Worker's push
/// sweep both evaluate it, and two copies of "is everything actually fine" would be two answers.
/// </para>
/// </remarks>
public static class QuietStretch
{
    /// <summary>
    /// How long the silence has to last before it is worth reporting. A day or two without an
    /// alert is just an ordinary day; a week is a stretch a caregiver has noticed and started
    /// wondering about.
    /// </summary>
    public const int MinimumDays = 7;

    /// <summary>
    /// Freshness tiers (<see cref="MemberInsightsCalculator.ComputeDataFreshness"/>) that still
    /// mean data is arriving. Amber and red are the tiers where the wearable has gone quiet, and
    /// a quiet wearable is why there are no alerts — the member's own state is unknown, not good.
    /// </summary>
    private static readonly string[] LiveFreshnessTiers = ["green", "blue"];

    /// <summary>
    /// Reads the stretch, or reports why there is nothing reassuring to say. Never throws on a
    /// half-populated context: an absent input is treated as "cannot claim it", which is the
    /// safe direction for every field here.
    /// </summary>
    public static QuietStretchVerdict Evaluate(QuietStretchContext context)
    {
        if (context.IsMonitoringPaused
            || !context.HasEstablishedBaseline
            || context.HasUnresolvedAlerts
            || !LiveFreshnessTiers.Contains(context.DataFreshnessTier))
        {
            return QuietStretchVerdict.None;
        }

        // Never alerted at all is still a quiet stretch — it just runs from when this member came
        // under watch rather than from an episode that ended. Both anchors are compared the same
        // way below, so a member with no history is not silently excluded from the good news.
        var since = context.LastAlertAtUtc ?? context.MonitoringSinceUtc;
        if (since > context.UtcNow)
            return QuietStretchVerdict.None;

        // Whole days only. A caregiver reads "7 days" as seven nights gone by, so 6.8 days is 6 —
        // rounding up would let the first week's message land a few hours early and, worse, let
        // the weekly push below fire twice inside one week.
        var days = (int)(context.UtcNow - since).TotalDays;
        return days < MinimumDays
            ? QuietStretchVerdict.None
            : new QuietStretchVerdict(true, days, since);
    }

    /// <summary>
    /// Which weekly all-clear this stretch is up to — 1 at seven days, 2 at fourteen, and so on.
    /// The Worker namespaces its dedup key with this, which is what paces the push to one a week
    /// per member without storing a "last reassured" column: the same week cannot produce two
    /// keys, and the next week cannot collide with the last one's.
    /// </summary>
    public static int WeeklyOccurrence(int quietDays) => quietDays / 7;
}

/// <param name="MonitoringSinceUtc">
/// When this member came under watch — the anchor for a member who has never had an alert at all.
/// Their record's creation is the honest floor: nothing could have been raised before it existed.
/// </param>
/// <param name="DataFreshnessTier">
/// green/blue/amber/red from <see cref="MemberInsightsCalculator.ComputeDataFreshness"/>, passed
/// as the caller already computed it rather than recomputed here, so the dashboard's freshness
/// caption and this verdict can never disagree about whether data is arriving.
/// </param>
public sealed record QuietStretchContext
{
    public required DateTime UtcNow { get; init; }
    public required bool IsMonitoringPaused { get; init; }
    public required bool HasEstablishedBaseline { get; init; }
    public required bool HasUnresolvedAlerts { get; init; }
    public required string DataFreshnessTier { get; init; }
    public DateTime? LastAlertAtUtc { get; init; }
    public required DateTime MonitoringSinceUtc { get; init; }
}

/// <param name="Days">Whole days of silence. Zero when <paramref name="IsQuiet"/> is false.</param>
/// <param name="SinceUtc">
/// What the stretch is measured from — the last alert, or the member coming under watch.
/// <see cref="DateTime.MinValue"/> when there is no stretch to report.
/// </param>
public readonly record struct QuietStretchVerdict(bool IsQuiet, int Days, DateTime SinceUtc)
{
    public static readonly QuietStretchVerdict None = new(false, 0, DateTime.MinValue);
}
