namespace CardiTrack.Mobile.Core.Offline;

/// <summary>
/// Whether a screen should say how old its data is.
/// </summary>
/// <remarks>
/// <para>
/// It should not, nearly always. Readings are pulled every ten minutes
/// (<c>min_pull_interval_minutes</c>, with dormancy backoff switched off), so a dashboard is
/// normally minutes old by design. Dating it unconditionally turned that into a caption on every
/// screen — "Updated 10 minutes ago" over data that is behaving exactly as intended — and a line
/// that appears when nothing is wrong cannot mean anything when something is.
/// </para>
/// <para>
/// So the age is shown only once it has outlived what the pipeline promises. The threshold is
/// three missed pulls rather than two: at a ten-minute cadence two is twenty minutes, which a
/// single slow provider response reaches on its own, and a caption that fires on ordinary jitter
/// is the thing this exists to remove.
/// </para>
/// <para>
/// <strong>What this measures is our pipeline, not the wearer.</strong> The timestamp behind it
/// moves when a poll succeeds, not when the watch reports — so a watch left on a charger keeps the
/// caption silent, because we are still polling successfully and finding nothing new. That gap is
/// the device-silence alerting's job and is deliberately not duplicated here; a caption that tried
/// to mean both would be reliable at neither.
/// </para>
/// </remarks>
public static class DataAge
{
    /// <summary>
    /// How old data has to be before its age is worth saying out loud. Three pulls at the
    /// ten-minute cadence the providers are configured for.
    /// </summary>
    /// <remarks>
    /// A constant rather than a per-connection figure because the cadence is not on the responses
    /// these screens load — it belongs to a device, and these screens are about a member, who may
    /// have several. Reading it properly would cost an extra round trip on every dashboard paint
    /// to discover a number that is the same for every connection in the product today. If a
    /// provider ever syncs on a genuinely different cadence, this becomes a server-supplied field
    /// rather than a bigger constant.
    /// </remarks>
    public static readonly TimeSpan WorthMentioning = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Whether data last refreshed at <paramref name="lastSyncedAtUtc"/> is old enough to say so.
    /// </summary>
    /// <remarks>
    /// False for null — a member who has never synced is a different statement, and the screens
    /// have their own copy for it. False for a timestamp in the future, which a clock adrift
    /// between the phone and the server can produce: the honest reading of "negative age" is that
    /// nothing is known to be wrong.
    /// </remarks>
    public static bool IsWorthShowing(DateTime? lastSyncedAtUtc, DateTime utcNow)
    {
        if (lastSyncedAtUtc is not { } synced)
            return false;

        var age = utcNow - DateTime.SpecifyKind(synced, DateTimeKind.Utc);
        return age >= WorthMentioning;
    }
}
