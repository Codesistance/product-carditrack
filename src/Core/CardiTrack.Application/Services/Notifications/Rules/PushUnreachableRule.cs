namespace CardiTrack.Application.Services.Notifications.Rules;

/// <summary>
/// OS permission denied/revoked, safety channel muted, every token dead 7 days, or the last send
/// hit a <c>Permanent</c> failure — the reachability picture <c>DeviceTokenService</c> maintains
/// on <see cref="NudgeContext.User"/>. A personal gap (§10.3): evaluated once per user, not once
/// per member, and fires even outside member scope.
/// </summary>
public sealed class PushUnreachableRule : INudgeRule
{
    public const string Code = "PUSH_UNREACHABLE";

    public string RuleCode => Code;
    public int Version => 1;
    public NudgeSpec Spec { get; } = NudgeSpec.Safety();

    public NudgeVerdict Evaluate(NudgeContext context)
    {
        // Personal gap — evaluated on the member-less context only, so it doesn't fire once per
        // watched relative for what is really one fact about this user's phone.
        if (context.Member is not null)
            return NudgeVerdict.NoGap;

        if (context.User.HasReachablePushDevice)
            return NudgeVerdict.NoGap;

        return NudgeVerdict.Gap(
            deepLink: "carditrack://settings/notifications",
            discriminator: "unreachable");
    }
}
