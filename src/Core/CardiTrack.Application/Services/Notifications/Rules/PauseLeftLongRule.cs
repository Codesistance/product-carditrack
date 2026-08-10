using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services.Notifications.Rules;

/// <summary>
/// Monitoring has been paused for a fortnight. Pauses are time-bounded precisely so nobody is left
/// silently unwatched — but a long one deserves a deliberate choice rather than drift.
/// </summary>
/// <remarks>
/// The one rule that evaluates <em>while</em> a member is paused. Every other rule goes quiet then:
/// nagging about a member someone deliberately paused is the fastest way to teach a caregiver to
/// ignore the whole surface.
/// </remarks>
public sealed class PauseLeftLongRule : INudgeRule
{
    public const string Code = "PAUSE_LEFT_LONG";

    public const int LongPauseDays = 14;

    public string RuleCode => Code;
    public int Version => 1;

    public NudgeSpec Spec { get; } = new()
    {
        Category = NotificationCategory.Account,
        Priority = NotificationPriority.Medium,
        DefaultSnooze = TimeSpan.FromDays(7),
        MaxSnooze = TimeSpan.FromDays(30),
        AppliesWhenPaused = true
    };

    public NudgeVerdict Evaluate(NudgeContext context)
    {
        var member = context.Member;
        if (member is null || member.MonitoringPausedUntil is null)
            return NudgeVerdict.NoGap;

        if (member.MonitoringPausedUntil <= context.UtcNow)
            return NudgeVerdict.NoGap;

        // Measured from when the pause was set, which we infer from how far ahead it still runs:
        // a pause with two weeks left on it was set for at least that long.
        var remaining = member.MonitoringPausedUntil.Value - context.UtcNow;
        if (remaining < TimeSpan.FromDays(LongPauseDays))
            return NudgeVerdict.NoGap;

        return NudgeVerdict.Gap(
            deepLink: $"carditrack://cardimembers/{member.Id}",
            discriminator: member.Id.ToString("N"),
            templateData: new Dictionary<string, object> { ["days"] = (int)remaining.TotalDays });
    }
}
