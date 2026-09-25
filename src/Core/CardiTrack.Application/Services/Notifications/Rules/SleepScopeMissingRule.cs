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
public sealed class SleepScopeMissingRule : ISetupStepRule
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
        var (check, target) = Assess(context);
        if (!check.IsNotDone)
            return NudgeVerdict.NoGap;

        return NudgeVerdict.Gap(
            deepLink: check.ActionDeepLink,
            discriminator: target.ToString("N"));
    }

    /// <summary>
    /// Applies only while the member has a connected device — that is what could grant sleep —
    /// and is done once any of them does. A member with no device has no sleep step at all,
    /// rather than one that reads as finished.
    /// </summary>
    public SetupCheck CheckSetup(NudgeContext context) => Assess(context).Check;

    /// <summary>
    /// The one predicate both answers come from, with the connection it is about: the one to fix
    /// when nothing grants sleep, or the one that does when something does.
    /// </summary>
    private static (SetupCheck Check, Guid Target) Assess(NudgeContext context)
    {
        if (context.Member is null)
            return (SetupCheck.NotApplicable, Guid.Empty);

        var live = context.Connections
            .Where(c => c.Status == ConnectionStatus.Connected)
            .OrderBy(c => c.Id)
            .ToList();

        if (live.Count == 0)
            return (SetupCheck.NotApplicable, Guid.Empty);

        // Any connection granting sleep covers the member — a second watch without it is not a gap.
        var granting = live.FirstOrDefault(c => DeviceScopes.GrantsSleep(c.Scopes));
        var target = granting ?? live[0];

        return (
            SetupCheck.Of(granting is not null, DeepLinkFor(context.Member.Id, target.Id)),
            target.Id);
    }

    private static string DeepLinkFor(Guid memberId, Guid connectionId) =>
        $"carditrack://cardimembers/{memberId}/devices/{connectionId}";
}
