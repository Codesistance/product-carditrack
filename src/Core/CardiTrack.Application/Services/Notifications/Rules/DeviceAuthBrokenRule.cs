using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services.Notifications.Rules;

/// <summary>
/// A device connection whose OAuth grant has failed. Monitoring is down, and from the dashboard it
/// looks identical to a quiet day — which is the whole reason this is Safety class.
/// </summary>
public sealed class DeviceAuthBrokenRule : INudgeRule
{
    public const string Code = "DEVICE_AUTH_BROKEN";

    public string RuleCode => Code;
    public int Version => 1;
    public NudgeSpec Spec { get; } = NudgeSpec.Safety() with { PushesWhenOpen = true };

    public NudgeVerdict Evaluate(NudgeContext context)
    {
        if (context.Member is null)
            return NudgeVerdict.NoGap;

        var broken = context.Connections
            .Where(c => c.Status is ConnectionStatus.TokenExpired or ConnectionStatus.AuthError)
            .OrderBy(c => c.Id)
            .FirstOrDefault();

        if (broken is null)
            return NudgeVerdict.NoGap;

        // The two causes need different words — an expired token reconnects silently, a revoked
        // grant means the wearer said no on Google's screen — but they are one gap, so one rule.
        var variant = broken.Status == ConnectionStatus.TokenExpired ? "expired" : "revoked";

        return NudgeVerdict.Gap(
            deepLink: $"carditrack://cardimembers/{context.Member.Id}/devices",
            discriminator: broken.Id.ToString("N"),
            variant: variant);
    }
}
