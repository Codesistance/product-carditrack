using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services.Notifications.Rules;

/// <summary>
/// No emergency contact number on the member, so the two things a caregiver would reach for in a
/// hurry — SOS and Call — have nowhere to go.
/// </summary>
/// <remarks>
/// <para>
/// The member's own detail page has said this all along, on a card nobody visits until they are
/// already looking. This puts it where the other unlocks are, because the moment it matters is
/// the one moment nobody is going to be browsing a profile.
/// </para>
/// <para>
/// <b>The number this asks for is <c>EmergencyContactPhone</c>, not <c>CardiMember.Phone</c>.</b>
/// The latter is vestigial — no screen sets it, and the dashboard's Call and SOS actions are both
/// wired to the emergency contact — so a rule that checked it would go on nudging families who
/// have already given the number that is actually used.
/// </para>
/// <para>
/// A day's grace before it fires. Adding a member and connecting their watch is already several
/// screens, and a nudge that appears while somebody is still in the middle of that is asking them
/// for something they are on their way to doing. Not gated on a baseline the way
/// <see cref="MedicalNotesEmptyRule"/> is: that rule waits because nothing can use the notes until
/// there is something to read them against, whereas a phone number is worth having from the first
/// day and the actions it unlocks are greyed out until it exists.
/// </para>
/// <para>
/// Unlock rather than Safety. It is a gap in what the app can do rather than a live event, and
/// Safety is the category that may interrupt someone — this should wait its turn on the dashboard
/// with the rest of "Complete the picture". High rather than Low within that, because the thing
/// it unlocks is reaching help, not a better report.
/// </para>
/// </remarks>
public sealed class EmergencyContactMissingRule : INudgeRule
{
    public const string Code = "EMERGENCY_CONTACT_MISSING";

    /// <summary>How long a new member is left alone before this is raised.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromDays(1);

    public string RuleCode => Code;
    public int Version => 1;

    public NudgeSpec Spec { get; } = new()
    {
        Category = NotificationCategory.Unlock,
        Priority = NotificationPriority.High,

        // Shorter than the medical-notes snooze, and with a ceiling. "Not now" about a phone
        // number is a fair answer; "never ask again" about the number the SOS button dials is a
        // setting nobody would knowingly choose from a dashboard card.
        DefaultSnooze = TimeSpan.FromDays(14),
        MaxSnooze = TimeSpan.FromDays(60)
    };

    public NudgeVerdict Evaluate(NudgeContext context)
    {
        var member = context.Member;
        if (member is null || member.HasEmergencyContact)
            return NudgeVerdict.NoGap;

        if (context.UtcNow - member.CreatedDate < Grace)
            return NudgeVerdict.NoGap;

        return NudgeVerdict.Gap(
            deepLink: $"carditrack://cardimembers/{member.Id}/edit#emergencyContact",
            discriminator: member.Id.ToString("N"));
    }
}
