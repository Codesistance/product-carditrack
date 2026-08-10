using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services.Notifications.Rules;

/// <summary>
/// A connected wearable whose grant does not include sleep. Sleep data never arrives, the sleep
/// baseline never forms, and nothing says so.
/// </summary>
/// <remarks>
/// The copy promises sleep <em>tracking and trends</em>, not sleep alerts: alert generation is not
/// built, and a nudge that trades a caregiver's effort for a capability we do not ship spends the
/// trust the whole engine runs on.
/// </remarks>
public sealed class SleepScopeMissingRule : INudgeRule
{
    public const string Code = "SLEEP_SCOPE_MISSING";

    public string RuleCode => Code;
    public int Version => 1;

    public NudgeSpec Spec { get; } = new()
    {
        Category = NotificationCategory.Unlock,
        Priority = NotificationPriority.High,
        DefaultSnooze = TimeSpan.FromDays(14),
        MaxSnooze = TimeSpan.FromDays(90)
    };

    public NudgeVerdict Evaluate(NudgeContext context)
    {
        if (context.Member is null)
            return NudgeVerdict.NoGap;

        var live = context.Connections
            .Where(c => c.Status == ConnectionStatus.Connected)
            .ToList();

        if (live.Count == 0)
            return NudgeVerdict.NoGap;

        // Any connection granting sleep covers the member — a second watch without it is not a gap.
        if (live.Any(c => DeviceScopes.GrantsSleep(c.Scopes)))
            return NudgeVerdict.NoGap;

        var target = live.OrderBy(c => c.Id).First();

        return NudgeVerdict.Gap(
            deepLink: $"carditrack://cardimembers/{context.Member.Id}/devices/{target.Id}",
            discriminator: target.Id.ToString("N"));
    }
}
