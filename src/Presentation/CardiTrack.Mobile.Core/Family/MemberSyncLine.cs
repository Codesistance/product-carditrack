using CardiTrack.Application.Services;
using CardiTrack.Mobile.Core.Forms;

namespace CardiTrack.Mobile.Core.Family;

/// <summary>How fresh a member's data is, as a word for the dot beside the line.</summary>
public enum SyncFreshness
{
    /// <summary>Heard from within the last few hours.</summary>
    Recent,

    /// <summary>Quiet for long enough to be worth noticing, short of a gap.</summary>
    Quiet,

    /// <summary>Nothing for half a day or more, or nothing ever.</summary>
    Gap,

    /// <summary>No device to hear from — not a fault of the data, so not coloured as one.</summary>
    NoDevice,
}

/// <summary>
/// The second line of a "Who we watch" card: when the member's device last sent anything.
/// </summary>
/// <remarks>
/// What a caregiver scanning the family wants from that list is "is everyone's watch still
/// talking to us", not the latest alert — the alert has its own screen, and a member card that
/// looked like an alert card read as a second copy of the Alerts tab. The thresholds are the
/// dashboard's own freshness tiers (<see cref="MemberInsightsCalculator.AmberStaleHours"/>,
/// <see cref="MemberInsightsCalculator.RedStaleHours"/>), so the dot on this card and the
/// dashboard's data banner never disagree about the same silence.
/// </remarks>
public static class MemberSyncLine
{
    public static (string Text, SyncFreshness Freshness) For(
        DateTime? lastSyncedAtUtc, int connectedDevices, DateTime nowUtc)
    {
        if (lastSyncedAtUtc is not { } synced)
        {
            return connectedDevices > 0
                ? ("Connected — waiting for the first sync", SyncFreshness.Gap)
                : ("No device connected", SyncFreshness.NoDevice);
        }

        var silence = nowUtc - DateTime.SpecifyKind(synced, DateTimeKind.Utc);
        var freshness =
            silence >= TimeSpan.FromHours(MemberInsightsCalculator.RedStaleHours) ? SyncFreshness.Gap
            : silence >= TimeSpan.FromHours(MemberInsightsCalculator.AmberStaleHours) ? SyncFreshness.Quiet
            : SyncFreshness.Recent;

        return ($"Last synced {RelativeTime.Format(synced)}", freshness);
    }
}
