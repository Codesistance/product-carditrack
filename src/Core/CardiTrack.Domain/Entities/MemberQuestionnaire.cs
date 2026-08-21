using CardiTrack.Domain.Common;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// One short question the service asked the family about a member, and their answer.
/// </summary>
/// <remarks>
/// <para>
/// The wearable measures; it does not know why. A member who has been sleeping badly for a week
/// looks the same to a device whether they changed rooms, changed routine, or changed nothing —
/// and the family knows which. These questions are how that reaches the model, generated from what
/// the readings actually show rather than asked on a schedule.
/// </para>
/// <para>
/// Deliberately not <c>ISoftDeletable</c>. Everywhere else on this platform a delete is a flag,
/// because a health record that vanishes is a record that cannot be audited. This is the opposite
/// case: the content is something a family member wrote about a person who never consented to the
/// service, and "delete" here has to mean the row is gone (GDPR Art. 17). Dismissing is the
/// non-destructive option, and it is a separate status for exactly that reason.
/// </para>
/// </remarks>
public class MemberQuestionnaire : BaseEntity
{
    public Guid CardiMemberId { get; set; }

    /// <summary>
    /// The question as asked. Encrypted at rest — it is derived from the member's readings and can
    /// name a condition, so it is the same class of data as the answer. See
    /// <c>QuestionnaireService</c> for where the encryption happens.
    /// </summary>
    public string QuestionText { get; set; } = string.Empty;

    /// <summary>The family's answer, encrypted at rest. Null until answered.</summary>
    public string? AnswerText { get; set; }

    /// <summary>
    /// Why this was asked, in one everyday sentence ("Yesterday looked quieter than usual").
    /// Stored as written, not encrypted: it describes a pattern in readings the service already
    /// holds, the same class of derived prose as <see cref="Alert.Message"/>. Shown to the family so
    /// a question never arrives looking arbitrary. Null when the model gave no reason worth showing.
    /// </summary>
    public string? TriggerContext { get; set; }

    public QuestionnaireStatus Status { get; set; } = QuestionnaireStatus.Pending;

    public DateTime GeneratedAtUtc { get; set; }

    public DateTime? AnsweredAtUtc { get; set; }

    /// <summary>Which caregiver answered — several may share a member, and answers can be edited.</summary>
    public Guid? AnsweredByUserId { get; set; }

    /// <summary>See <see cref="QuestionnaireScope"/>. Defaults to the pre-existing recency-decay
    /// behaviour, so rows written before this distinction existed carry on exactly as they did.</summary>
    public QuestionnaireScope Scope { get; set; } = QuestionnaireScope.TimeScoped;

    /// <summary>
    /// When a <see cref="QuestionnaireScope.TimeScoped"/> answer stops being read back into prompts
    /// — set once, at generation, from a fixed duration rather than a date the model guessed at (see
    /// <c>DigestGenerationService.TimeScopedAnswerLifetime</c>). Null for
    /// <see cref="QuestionnaireScope.Permanent"/>, which never expires on its own.
    /// </summary>
    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary>
    /// The last moment this is still worth <em>asking</em>, as opposed to
    /// <see cref="ExpiresAtUtc"/>, which is how long the answer keeps informing prompts once given.
    /// Null for <see cref="QuestionnaireScope.Permanent"/> questions and for rows written before
    /// this distinction existed, both of which stay askable indefinitely.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two clocks are genuinely different and conflating them is what produced the failure this
    /// column exists for. A time-scoped question asks about the present moment — "did he feel tired
    /// at all today?" — and stops making sense the instant that day ends, while the answer to it
    /// stays useful for weeks. With only the answer's clock, a question generated one evening was
    /// still <see cref="QuestionnaireStatus.Pending"/> the next morning and still on the member's
    /// screen, asking a caregiver about a "today" that was already over.
    /// </para>
    /// <para>
    /// Held as a UTC instant rather than a local date so no reader needs the member's timezone to
    /// judge it: the pipeline resolves the anchor zone once, at generation, and every consumer after
    /// that — the API, the expiry job, the phone — compares against its own clock.
    /// </para>
    /// </remarks>
    public DateTime? AskableUntilUtc { get; set; }

    /// <summary>
    /// How many pushes have gone out for this question — the original ask plus any reminders.
    /// Zero until <c>QuestionnaireAlertWorker</c>'s first sweep. Capped at
    /// <c>QuestionnaireAlertWorker.MaxPushes</c> so a <see cref="QuestionnaireScope.Permanent"/>
    /// question, which never lapses on its own, cannot nag a family forever.
    /// </summary>
    public int ReminderCount { get; set; }

    /// <summary>
    /// When the last push for this question went out. Null until the first one does.
    /// <c>QuestionnaireAlertWorker</c> reads this both to find questions due for another push and,
    /// via a conditional update keyed on <see cref="ReminderCount"/>, to claim one before sending it
    /// — the same claim-then-send discipline <c>NotificationDispatchWorker</c> uses, since the
    /// Worker runs several instances and a plain read-then-write here would risk a duplicate push.
    /// </summary>
    public DateTime? LastRemindedAtUtc { get; set; }

    /// <summary>
    /// Whether this is a question that has stopped being worth asking: still waiting on the family,
    /// and past the moment it was about.
    /// </summary>
    /// <remarks>
    /// On the entity rather than in one service because four places need the same verdict — the
    /// listing endpoint refusing to serve it, the endpoint that retires it, the worker that sweeps
    /// members whose family never opened the app, and the phone deciding not to draw the card. A
    /// rule this small, restated four times, is a rule that will eventually disagree with itself.
    /// </remarks>
    public bool HasLapsed(DateTime utcNow) =>
        Status == QuestionnaireStatus.Pending && AskableUntilUtc is { } until && until <= utcNow;
}
