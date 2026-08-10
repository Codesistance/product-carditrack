using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services.Notifications.Rules;

/// <summary>
/// A member stuck partway through baseline learning: not enough days captured for the coverage
/// gate, and nothing new arriving. Until the baseline forms, no alert can ever fire for them.
/// </summary>
/// <remarks>
/// Progress is counted in days that produced data, not days elapsed — a watch in a drawer for a
/// fortnight has taught us nothing — which is why a stall is detectable at all.
/// </remarks>
public sealed class BaselineStalledRule : INudgeRule
{
    public const string Code = "BASELINE_STALLED";

    /// <summary>No new data for this long, with the window unfilled, is a stall rather than a lull.</summary>
    public const int StalledAfterDays = 7;

    /// <summary>Matches <see cref="BaselineCalculator"/>'s 80% coverage gate over 30 days.</summary>
    public const int DaysRequired = 24;

    public string RuleCode => Code;
    public int Version => 1;

    public NudgeSpec Spec { get; } = new()
    {
        Category = NotificationCategory.Blocking,
        Priority = NotificationPriority.High,
        DefaultSnooze = TimeSpan.FromDays(14),
        MaxSnooze = TimeSpan.FromDays(30)
    };

    public NudgeVerdict Evaluate(NudgeContext context)
    {
        var member = context.Member;
        if (member is null || member.HasEstablishedBaseline)
            return NudgeVerdict.NoGap;

        if (member.DaysCaptured >= DaysRequired)
            return NudgeVerdict.NoGap;

        // A member who has never produced a day is either brand new or has no working device —
        // both are other rules' business, and saying "0/30 days captured" to someone whose watch
        // was never connected is noise on top of the gap they actually have.
        if (member.LastActivityDate is null)
            return NudgeVerdict.NoGap;

        var daysSinceData = (DateOnly.FromDateTime(context.UtcNow).DayNumber
                             - member.LastActivityDate.Value.DayNumber);

        if (daysSinceData < StalledAfterDays)
            return NudgeVerdict.NoGap;

        return NudgeVerdict.Gap(
            deepLink: $"carditrack://cardimembers/{member.Id}/baseline",
            discriminator: member.Id.ToString("N"),
            templateData: new Dictionary<string, object>
            {
                ["captured"] = member.DaysCaptured,
                ["required"] = BaselineProgress.PeriodDays
            });
    }
}
