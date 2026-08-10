namespace CardiTrack.Application.Services.Notifications.Rules;

/// <summary>
/// A member who had a wearable and no longer has one. Nothing is being collected, so every other
/// feature quietly stops working.
/// </summary>
/// <remarks>
/// Deliberately not "has no device": a member who never connected one is still inside onboarding,
/// and the onboarding flow owns that conversation. Requiring
/// <see cref="NudgeMemberSnapshot.EverHadConnection"/> is what keeps this rule from firing at
/// someone who is halfway through Step 6.
/// </remarks>
public sealed class DeviceRemovedRule : INudgeRule
{
    public const string Code = "DEVICE_REMOVED";

    public string RuleCode => Code;
    public int Version => 1;

    public NudgeSpec Spec { get; } = new()
    {
        Category = Domain.Enums.NotificationCategory.Blocking,
        Priority = Domain.Enums.NotificationPriority.Critical,
        DefaultSnooze = TimeSpan.FromDays(7),
        MaxSnooze = TimeSpan.FromDays(30),
        // A new account that has already lost its only connection is precisely who needs telling.
        AppliesDuringGracePeriod = true
    };

    public NudgeVerdict Evaluate(NudgeContext context)
    {
        if (context.Member is null || !context.Member.EverHadConnection)
            return NudgeVerdict.NoGap;

        if (context.Connections.Count > 0)
            return NudgeVerdict.NoGap;

        return NudgeVerdict.Gap(
            deepLink: $"carditrack://cardimembers/{context.Member.Id}/devices",
            discriminator: context.Member.Id.ToString("N"));
    }
}
