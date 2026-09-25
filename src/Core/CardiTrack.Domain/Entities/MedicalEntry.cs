using CardiTrack.Domain.Common;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// One line of a CardiMember's medical information — a condition, an allergy, a medication — with
/// who put it on file, when, and when somebody last said it still holds.
/// </summary>
/// <remarks>
/// <para>
/// A ledger rather than the single free-text note this replaced. Separate lines can each be
/// confirmed, changed or taken off without retyping the rest, and each keeps its own dates: one
/// allergy confirmed last week says nothing about a medication nobody has looked at in a year.
/// </para>
/// <para>
/// Changing or removing a line does not delete it. It leaves the current list and stays in the
/// member's history (<see cref="RemovedAtUtc"/>, and <see cref="ReplacedByEntryId"/> when an edit
/// superseded it), so a family can see what the notes used to say and when that changed. Erasing
/// is separate and real: <c>MedicalEntryService.EraseAsync</c> deletes the row, because these are
/// a family's words about somebody who never consented to the service, and "delete" on request has
/// to mean gone (GDPR Art. 17) — the same stance <see cref="MemberQuestionnaire"/> records. For the
/// same reason this is not <c>ISoftDeletable</c>: that filter would hide the history this keeps on
/// purpose.
/// </para>
/// <para>
/// <see cref="CardiMember.MedicalNotes"/> and <see cref="CardiMember.MedicalNotesReviewedAtUtc"/>
/// are kept as a summary of the current lines, rewritten with every change, so everything that read
/// the single note — the AI prompt, the nudge rules, app builds that predate the ledger — carries on
/// reading it unchanged.
/// </para>
/// </remarks>
public class MedicalEntry : BaseEntity
{
    public Guid CardiMemberId { get; set; }

    public MedicalEntryKind Kind { get; set; } = MedicalEntryKind.Other;

    /// <summary>What the line says. Encrypted at rest — see <c>MedicalEntryService</c>.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// When the line went on file. For a line carried over from the single note, the note's own
    /// review date or, failing that, when the member was added — the best date there is.
    /// </summary>
    public DateTime AddedAtUtc { get; set; }

    /// <summary>
    /// Which caregiver wrote it. Null for a line carried over from the single note, which recorded
    /// no author, and after that caregiver's account is erased.
    /// </summary>
    public Guid? AddedByUserId { get; set; }

    /// <summary>
    /// When a caregiver last said this line still holds, or null when nobody ever has — a line
    /// carried over from a note that was never reviewed. "Last confirmed", in the sense
    /// <see cref="CardiMember.MedicalNotesReviewedAtUtc"/> documents.
    /// </summary>
    public DateTime? ConfirmedAtUtc { get; set; }

    /// <summary>When the line left the current list, or null while it is on it.</summary>
    public DateTime? RemovedAtUtc { get; set; }

    /// <summary>Which caregiver took it off, or null while it is current or after their erasure.</summary>
    public Guid? RemovedByUserId { get; set; }

    /// <summary>
    /// The line that replaced this one when it was edited, or null when it was simply removed (or
    /// is still current). What lets the history say "changed" rather than "removed".
    /// </summary>
    public Guid? ReplacedByEntryId { get; set; }

    public bool IsCurrent => RemovedAtUtc is null;
}
