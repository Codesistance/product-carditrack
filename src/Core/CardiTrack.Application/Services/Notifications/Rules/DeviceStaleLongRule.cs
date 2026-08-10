using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services.Notifications.Rules;

/// <summary>
/// A connected wearable that has not delivered anything for two days — flat battery, left in a
/// drawer, or a phone that stopped syncing.
/// </summary>
/// <remarks>
/// Forty-eight hours, not two: the AI pipeline's real-time device check (llm_design.md) owns the
/// short horizon and pushes at two hours of silence. This is the longer, calmer signal, and the two
/// are kept apart so a caregiver is not told the same thing twice by two systems.
/// </remarks>
public sealed class DeviceStaleLongRule : INudgeRule
{
    public const string Code = "DEVICE_STALE_LONG";

    /// <summary>Silence beyond this is worth mentioning. A day is normal; two is not.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(48);

    public string RuleCode => Code;
    public int Version => 1;

    public NudgeSpec Spec { get; } = new()
    {
        Category = NotificationCategory.Blocking,
        Priority = NotificationPriority.High,
        DefaultSnooze = TimeSpan.FromDays(3),
        MaxSnooze = TimeSpan.FromDays(14)
    };

    public NudgeVerdict Evaluate(NudgeContext context)
    {
        if (context.Member is null)
            return NudgeVerdict.NoGap;

        // A broken grant is a different, louder gap. Reporting both would have the caregiver fix
        // the battery on a watch whose real problem is that we lost permission to read it.
        var connected = context.Connections
            .Where(c => c.Status == ConnectionStatus.Connected)
            .ToList();

        if (connected.Count == 0)
            return NudgeVerdict.NoGap;

        // Any one device still reporting means data is flowing; only a wholly silent member counts.
        var mostRecent = connected.Max(c => c.LastSyncDate);
        if (mostRecent is null || context.UtcNow - mostRecent >= StaleAfter)
        {
            var stalest = connected.OrderBy(c => c.LastSyncDate ?? DateTime.MinValue).First();
            var hours = mostRecent is null ? (int?)null : (int)(context.UtcNow - mostRecent.Value).TotalHours;

            return NudgeVerdict.Gap(
                deepLink: $"carditrack://cardimembers/{context.Member.Id}/devices",
                discriminator: stalest.Id.ToString("N"),
                templateData: hours is null
                    ? new Dictionary<string, object>()
                    : new Dictionary<string, object> { ["hours"] = hours.Value });
        }

        return NudgeVerdict.NoGap;
    }
}
