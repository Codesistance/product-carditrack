using CardiTrack.Domain.Enums;
using CardiTrack.Domain.Extensions;

namespace CardiTrack.Application.Services.Notifications.Rules;

/// <summary>
/// A device connection whose OAuth grant has failed. Monitoring is down, and from the dashboard it
/// looks identical to a quiet day — which is the whole reason this is Safety class.
/// </summary>
/// <remarks>
/// This is the caregiver's "needs reconnecting" alert, and it replaces "gone quiet" rather than
/// joining it: while it stands, <c>DEVICE_STALE_LONG</c> and the device-silence alert both stand
/// down for the member (<see cref="ConnectionStatusExtensions.NeedsReconnect"/> is the one test all
/// three share). Besides the daily sweep and every gap re-evaluation, it is opened by
/// <c>InactivityDetectionService</c> at the moment that pass would otherwise have raised the silence
/// alert, and it resolves when a reconnect or the auth-recovery probe restores the grant.
/// </remarks>
public sealed class DeviceAuthBrokenRule : INudgeRule
{
    public const string Code = "DEVICE_AUTH_BROKEN";

    /// <summary>
    /// The template-data key naming the kind of device ("Fitbit", "Google Pixel Watch"), so the
    /// copy can say whose <em>what</em> needs reconnecting. A device kind is not a name or a
    /// reading, so it is as safe on the row as the counters other rules store.
    /// </summary>
    public const string DeviceKey = "device";

    public string RuleCode => Code;
    public int Version => 1;
    public NudgeSpec Spec { get; } = NudgeSpec.Safety() with { PushesWhenOpen = true };

    public NudgeVerdict Evaluate(NudgeContext context)
    {
        if (context.Member is null)
            return NudgeVerdict.NoGap;

        var broken = context.Connections
            .Where(c => c.Status.NeedsReconnect())
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
            templateData: new Dictionary<string, object> { [DeviceKey] = DeviceKind(broken.DeviceType) },
            variant: variant);
    }

    /// <summary>
    /// The device's display name, ready to drop into "{name}'s {device} needs reconnecting".
    /// <see cref="DeviceType.Other"/> becomes "device" — "Pop's Other" is not a sentence.
    /// </summary>
    internal static string DeviceKind(DeviceType deviceType) =>
        deviceType == DeviceType.Other ? "device" : deviceType.GetDisplayName();
}
