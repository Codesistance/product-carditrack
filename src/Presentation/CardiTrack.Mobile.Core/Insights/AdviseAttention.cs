using System.Globalization;

namespace CardiTrack.Mobile.Core.Insights;

/// <summary>
/// Whether the Dashboard card's sparkle button should still be pulsing for a member's wellness
/// suggestion. The pulse announces a suggestion the caregiver has not read yet; once the Quick
/// actions card has shown it, the same suggestion pulsing on every dashboard visit is the button
/// crying wolf — so the Details page records the suggestion's generation stamp as seen, and the
/// card animates only for a stamp newer than that record.
/// </summary>
/// <remarks>
/// Lives here rather than in the control for the reason <c>TrendScale</c> gives: the MAUI project
/// cannot be unit tested, and the arguable part of this is the comparison, not the drawing. The
/// store itself is the app's <c>Preferences</c> — per device, like <c>MetricTrendCard</c>'s
/// "explanation opened" flag: seen is a fact about this caregiver's screen, not about the member,
/// and it must not need a server round trip to answer.
/// </remarks>
public static class AdviseAttention
{
    /// <summary>Preferences key holding the last generation stamp this device has shown.</summary>
    public static string SeenKey(Guid memberId) => $"AdviseSeen:{memberId:D}";

    /// <summary>The value the Details page stores once the suggestion has been on screen.</summary>
    public static string Stamp(DateTimeOffset generatedAt) =>
        generatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// True while the suggestion the dashboard reports is one this device has not shown yet —
    /// no record, an unreadable record, or a record older than the reported generation. A server
    /// that predates the stamp reports null; that reads as unseen, which keeps the pulse's old
    /// always-on behaviour rather than silencing it on stale data.
    /// </summary>
    public static bool IsUnseen(DateTimeOffset? generatedAt, string? seenStamp)
    {
        if (generatedAt is null || string.IsNullOrWhiteSpace(seenStamp))
            return true;

        if (!DateTimeOffset.TryParse(
                seenStamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var seen))
        {
            return true;
        }

        return generatedAt.Value.UtcDateTime > seen.UtcDateTime;
    }
}
