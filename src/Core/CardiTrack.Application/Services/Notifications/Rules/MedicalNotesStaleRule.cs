using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services.Notifications.Rules;

/// <summary>
/// Medical notes are on file but nobody has said they are still true for half a year. Conditions
/// change, medications change, and a health background nobody has revisited is read by every
/// insight as though it were current.
/// </summary>
/// <remarks>
/// <para>
/// The other half of <see cref="MedicalNotesEmptyRule"/>. That rule asks a family to write the
/// background down once; this one is the only thing that ever asks again. Without it the notes
/// are a one-time question, and the longer a member is monitored the less the answer means.
/// </para>
/// <para>
/// Low priority and fully silenceable, for the same reason as the empty rule: a family whose
/// notes genuinely have not changed in years is not doing anything wrong, and being asked
/// forever about it is how a caregiver learns to ignore the whole surface. It never pushes —
/// there is no hour of any day at which this needs to interrupt someone.
/// </para>
/// </remarks>
public sealed class MedicalNotesStaleRule : INudgeRule
{
    public const string Code = "MEDICAL_NOTES_STALE";

    /// <summary>
    /// How long a confirmed background stays current. Six months is roughly the cadence of a
    /// routine review appointment, which is the occasion most likely to have changed anything
    /// worth recording here.
    /// </summary>
    public static readonly TimeSpan ReviewInterval = TimeSpan.FromDays(183);

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

        // Nothing on file is the empty rule's gap, not this one. The two are mutually exclusive by
        // construction, so a family is never asked to write the background down and to confirm it
        // in the same breath.
        if (member is null || !member.HasMedicalNotes)
            return NudgeVerdict.NoGap;

        // Same gate the empty rule applies. Until there is a baseline, nothing is reading the
        // notes yet, so asking a caregiver to re-verify them buys the member nothing.
        if (!member.HasEstablishedBaseline)
            return NudgeVerdict.NoGap;

        // Notes that predate the review date are the ones most likely to be out of date, so they
        // are asked about rather than exempted — but from when the member joined, not from the
        // deploy that added the column. Anchoring on "now" would have every existing family go
        // quiet for another six months, which is the opposite of what this rule is for, and
        // backfilling the column instead would have been the database claiming a review that
        // never happened.
        var anchor = member.MedicalNotesReviewedAtUtc ?? member.CreatedDate;
        var age = context.UtcNow - anchor;
        if (age < ReviewInterval)
            return NudgeVerdict.NoGap;

        // Never confirmed at all is a different sentence from "confirmed, but a while ago" — we
        // genuinely do not know how old those notes are, and a card claiming a figure we inferred
        // from a join date would be stating something we cannot support.
        if (member.MedicalNotesReviewedAtUtc is null)
        {
            return NudgeVerdict.Gap(
                deepLink: DeepLinkFor(member.Id),
                discriminator: member.Id.ToString("N"),
                variant: "never_confirmed");
        }

        // Months rather than days: "confirmed 209 days ago" invites arithmetic, and the rule does
        // not fire precisely enough for the extra resolution to mean anything.
        var months = (int)(age.TotalDays / 30);

        return NudgeVerdict.Gap(
            deepLink: DeepLinkFor(member.Id),
            discriminator: member.Id.ToString("N"),
            templateData: new Dictionary<string, object> { ["months"] = months });
    }

    /// <summary>
    /// The same link the empty rule uses. Both gaps close on the same screen, and the fragment is
    /// what tells a client to open the notes themselves rather than the top of the profile form.
    /// </summary>
    private static string DeepLinkFor(Guid memberId) =>
        $"carditrack://cardimembers/{memberId}/edit#medicalNotes";
}
