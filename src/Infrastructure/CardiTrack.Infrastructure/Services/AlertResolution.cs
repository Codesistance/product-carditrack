using CardiTrack.Domain.Entities;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// Closes automated alerts whose condition has passed.
/// </summary>
/// <remarks>
/// <para>
/// Every automated producer's cooldown is written as "one unresolved alert of this rule at a
/// time — resolving re-arms the check" (see <see cref="AlertRuleMarkers.Suppresses"/>). That
/// design is sound, but nothing in the system ever set <see cref="Alert.IsResolved"/>: there is no
/// resolve endpoint, and acknowledging records who looked, not that the episode ended. So every
/// rule fired exactly once per member and then latched shut for good — which is why a family that
/// had already seen one alert never saw another.
/// </para>
/// <para>
/// Resolution belongs with the producer rather than with the reader, because only the producer
/// knows what "over" means for its own rule: the device reporting again, the next window coming
/// back below the threshold, the day's readings no longer tripping it. A resolved alert is not
/// deleted or hidden — the lists still carry it, in the archive — so this closes an episode
/// rather than discarding a record.
/// </para>
/// </remarks>
internal static class AlertResolution
{
    /// <summary>
    /// Marks every already-unresolved alert matching <paramref name="isOver"/> resolved, and
    /// reports how many changed so the caller can skip a pointless save.
    /// </summary>
    /// <remarks>
    /// The entities come from the tracked repository read the producers already do, so mutating
    /// them and saving is the whole write — no second round trip to fetch them again.
    /// </remarks>
    internal static int Resolve(IEnumerable<Alert> alerts, Func<Alert, bool> isOver, DateTime utcNow)
    {
        var resolved = 0;

        foreach (var alert in alerts)
        {
            if (alert.IsResolved || !isOver(alert))
                continue;

            alert.IsResolved = true;
            alert.UpdatedDate = utcNow;
            resolved++;
        }

        return resolved;
    }
}
