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
    /// Why this was asked, in one plain sentence ("Sleep has been shorter than usual all week").
    /// Stored as written, not encrypted: it describes a pattern in readings the service already
    /// holds, the same class of derived prose as <see cref="Alert.Message"/>. Shown to the family so
    /// a question never arrives looking arbitrary.
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
}
