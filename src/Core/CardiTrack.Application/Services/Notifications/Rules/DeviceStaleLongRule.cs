using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services.Notifications.Rules;

/// <summary>
/// A connected wearable that has not delivered anything for two days — flat battery, left in a
/// drawer, or a phone that stopped syncing.
/// </summary>
/// <remarks>
/// Forty-eight hours, not two: <c>InactivityDetectionWorker</c> owns the short horizon and raises a
/// device-silence alert within two hours of a quiet wearable. This is the longer, calmer signal for
/// the case that worker cannot see — a member with no granular series at all, whose silence never
/// registers as silence there. Whenever its alert <em>is</em> standing, this rule defers, so a
/// caregiver is never told the same thing twice by two systems.
/// </remarks>
public sealed class DeviceStaleLongRule : INudgeRule
{
    public const string Code = "DEVICE_STALE_LONG";

    /// <summary>Silence beyond this is worth mentioning. A day is normal; two is not.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(48);

    public string RuleCode => Code;
    public int Version => 1;

    /// <remarks>
    /// The first pushing rule that is not safety-category, which §6.2 had described as the only
    /// kind. Two days of silence does mean monitoring is not working, so the push is warranted —
    /// but promoting the rule to Safety to earn it would also override quiet hours and remove the
    /// mute, and a condition that has already stood for forty-eight hours does not justify waking
    /// a household at 3am. Staying Blocking keeps the nudge channel's short ding and the
    /// quiet-hours deferral, which is the right volume for "this has been wrong for two days" as
    /// against "something is happening now".
    /// </remarks>
    public NudgeSpec Spec { get; } = new()
    {
        Category = NotificationCategory.Blocking,
        Priority = NotificationPriority.High,
        DefaultSnooze = TimeSpan.FromDays(3),
        MaxSnooze = TimeSpan.FromDays(14),
        PushesWhenOpen = true
    };

    public NudgeVerdict Evaluate(NudgeContext context)
    {
        if (context.Member is null)
            return NudgeVerdict.NoGap;

        // The device-silence alert is faster and louder, and asks for the same thing. While it
        // stands, this rule has nothing to add.
        if (context.Member.HasOpenDeviceSilenceAlert)
            return NudgeVerdict.NoGap;

        // A broken grant is a different, louder gap. Reporting both would have the caregiver fix
        // the battery on a watch whose real problem is that we lost permission to read it.
        //
        // SyncError is not one of those: the grant is intact and the provider simply failed to
        // answer. Testing for Connected alone excluded it, so a watch stuck failing for days
        // raised nothing here at all — a silent hole between this rule and DEVICE_AUTH_BROKEN,
        // which only covers TokenExpired and AuthError. The staleness test below is what decides
        // whether it matters: a connection that errored once and recovered has a fresh
        // LastSyncDate and still returns NoGap.
        var pairedAndPullable = context.Connections
            .Where(c => c.Status is ConnectionStatus.Connected or ConnectionStatus.SyncError)
            .ToList();

        if (pairedAndPullable.Count == 0)
            return NudgeVerdict.NoGap;

        // Any one device still reporting means data is flowing; only a wholly silent member counts.
        var mostRecent = pairedAndPullable.Max(c => c.LastSyncDate);
        if (mostRecent is null || context.UtcNow - mostRecent >= StaleAfter)
        {
            var stalest = pairedAndPullable.OrderBy(c => c.LastSyncDate ?? DateTime.MinValue).First();

            // A connection can be Connected with no LastSyncDate at all — never having synced once
            // is a different fact than an "{hours} ago" gap, and gets its own variant rather than a
            // template with nothing to fill it.
            if (mostRecent is null)
            {
                return NudgeVerdict.Gap(
                    deepLink: $"carditrack://cardimembers/{context.Member.Id}/devices",
                    discriminator: stalest.Id.ToString("N"),
                    variant: "never_synced");
            }

            var hours = (int)(context.UtcNow - mostRecent.Value).TotalHours;

            return NudgeVerdict.Gap(
                deepLink: $"carditrack://cardimembers/{context.Member.Id}/devices",
                discriminator: stalest.Id.ToString("N"),
                templateData: new Dictionary<string, object> { ["hours"] = hours });
        }

        return NudgeVerdict.NoGap;
    }
}
