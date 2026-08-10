using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services.Notifications.Rules;

/// <summary>
/// No medical notes recorded. Both features this unlocks — AI insights and the doctor-visit
/// report — ship today, so the promise is one we can keep.
/// </summary>
/// <remarks>
/// Low priority and fully silenceable by design. Some families will not want to write this down,
/// and that is a legitimate answer to be asked once and then left alone about.
/// </remarks>
public sealed class MedicalNotesEmptyRule : INudgeRule
{
    public const string Code = "MEDICAL_NOTES_EMPTY";

    public string RuleCode => Code;
    public int Version => 1;

    public NudgeSpec Spec { get; } = new()
    {
        Category = NotificationCategory.Unlock,
        Priority = NotificationPriority.Low,
        DefaultSnooze = TimeSpan.FromDays(30),
        MaxSnooze = TimeSpan.FromDays(90)
    };

    public NudgeVerdict Evaluate(NudgeContext context)
    {
        var member = context.Member;
        if (member is null || member.HasMedicalNotes)
            return NudgeVerdict.NoGap;

        // Nothing to compare against yet — asking for clinical context before we can use it is a
        // demand without a return.
        if (!member.HasEstablishedBaseline)
            return NudgeVerdict.NoGap;

        return NudgeVerdict.Gap(
            deepLink: $"carditrack://cardimembers/{member.Id}/edit#medicalNotes",
            discriminator: member.Id.ToString("N"));
    }
}
