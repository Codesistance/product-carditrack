using CardiTrack.Domain.Common;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// One caregiver's answer to one alert — what they did about it, and optionally a line saying more.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Append-only.</strong> A second caregiver answering adds a row; nothing is ever
/// overwritten. That is the whole point of the table: with more than one person watching, "what
/// did the family do about this" is a sequence of actions by named people, and collapsing it to a
/// single latest value would lose exactly the part that stops two people phoning at once.
/// </para>
/// <para>
/// The alert row keeps its own first-wins <c>AcknowledgedByUserId</c> untouched by this. That
/// field answers "is this handled", which is a single fact; this table answers "by whom, and what
/// did they say", which is not.
/// </para>
/// </remarks>
public class AlertResponse : BaseEntity
{
    public Guid AlertId { get; set; }

    /// <summary>
    /// The caregiver who answered, or null once that account has been erased.
    /// </summary>
    /// <remarks>
    /// Nullable for erasure, and for the same reason <c>Alert.AcknowledgedByUserId</c> is: the
    /// answer belongs to the member, who may still be watched by somebody else, so only the name
    /// of the caregiver who gave it goes. Deleting the row instead would take the family's record
    /// of what was done about a person they are still looking after.
    /// </remarks>
    public Guid? UserId { get; set; }

    public AlertResponseKind Kind { get; set; }

    /// <summary>
    /// The canned answer they picked, from <c>AlertResponseCatalog</c>, or null when they only
    /// wrote a note. Stored as the code rather than the label so re-wording a chip does not
    /// re-write what somebody already said.
    /// </summary>
    public string? ResponseCode { get; set; }

    /// <summary>
    /// Their own words, capped at 500 characters before encryption, or null.
    /// </summary>
    /// <remarks>
    /// Free text about the wearer — "she had a cold but she's fine" is health information about a
    /// named person — so it is AES-encrypted at rest, the same treatment
    /// <c>CardiMember.MedicalNotes</c> gets, and the column is deliberately unbounded because the
    /// ciphertext of 500 multi-byte characters runs several times longer than the plain text.
    /// </remarks>
    public string? Note { get; set; }
}
