using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services.Notifications.Rules;

/// <summary>
/// The account is still on the <c>"UTC"</c> default, so every statement CardiTrack makes about
/// "today" or "this morning" is being made in the wrong clock.
/// </summary>
/// <remarks>
/// <para>
/// UTC is the column default, derived from <c>Accept-Language</c> when nothing better is known —
/// not a choice anyone made. It observes no daylight saving, so it is wrong for a British user for
/// half the year and wrong for everyone else all of it.
/// </para>
/// <para>
/// This matters more than it looks: "no morning activity by 11am" is a red alert type, and
/// evaluated against UTC for a caregiver in Los Angeles it fires at 3am local.
/// </para>
/// </remarks>
public sealed class TimezoneDefaultRule : INudgeRule
{
    public const string Code = "TIMEZONE_DEFAULT";
    public const string DefaultTimeZoneId = "UTC";

    public string RuleCode => Code;
    public int Version => 1;

    public NudgeSpec Spec { get; } = new()
    {
        Category = NotificationCategory.Blocking,
        Priority = NotificationPriority.High,
        DefaultSnooze = TimeSpan.FromDays(30),
        MaxSnooze = TimeSpan.FromDays(90)
    };

    public NudgeVerdict Evaluate(NudgeContext context)
    {
        // Account-scoped: asked once of the user, not once per member they watch.
        if (context.Member is not null)
            return NudgeVerdict.NoGap;

        if (!string.Equals(context.User.TimeZoneId, DefaultTimeZoneId, StringComparison.OrdinalIgnoreCase))
            return NudgeVerdict.NoGap;

        return NudgeVerdict.Gap(
            deepLink: "carditrack://settings/profile#timezone",
            discriminator: context.User.Id.ToString("N"));
    }
}
