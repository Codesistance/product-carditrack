using System.ComponentModel.DataAnnotations;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.DTOs.Requests;

/// <summary>
/// The M1-14 edit form. A full replacement, not a patch: every field the form shows is sent
/// back, so clearing the medical notes or the emergency contact is expressed as an empty
/// value rather than being indistinguishable from "leave it alone".
/// </summary>
public class UpdateCardiMemberRequest
{
    [Required(ErrorMessage = "Name is required")]
    [StringLength(100, MinimumLength = 2)]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "Date of birth is required")]
    public DateOnly DateOfBirth { get; set; }

    /// <summary>
    /// Optional, unlike on create. Omitted or unset means "leave the stored value alone" — which is
    /// what makes this field the correction path for the members who predate the onboarding form
    /// asking for sex, without forcing a client that does not yet show the picker to guess.
    /// </summary>
    /// <remarks>
    /// The one field on this otherwise full-replacement form that is not a replacement. A client
    /// sending the M1-14 form without a sex picker would otherwise deserialise <c>0</c> here and
    /// overwrite a stated sex with nothing — a silent clinical regression on every unrelated edit,
    /// since sex is what the prompt layer reads to pick a reference range and a pronoun.
    /// </remarks>
    public Gender? Gender { get; set; }

    /// <summary>
    /// Optional. Omitted or unset means "not stated", which is <see cref="RelationshipType.Other"/> —
    /// the same value the read paths already fall back to when a caregiver has no link recorded.
    /// Defaulted here rather than left at <c>0</c>, which is not a member of the enum and would
    /// fail <c>IsInEnum</c> for a caller who simply had nothing to say.
    /// </summary>
    public RelationshipType RelationshipType { get; set; } = RelationshipType.Other;

    [EmailAddress(ErrorMessage = "Invalid email format")]
    public string? Email { get; set; }

    [Phone(ErrorMessage = "Invalid phone number")]
    public string? Phone { get; set; }

    [StringLength(100)]
    public string? EmergencyContactName { get; set; }

    [Phone(ErrorMessage = "Invalid emergency contact phone")]
    public string? EmergencyContactPhone { get; set; }

    [StringLength(2000)]
    public string? MedicalNotes { get; set; }

    /// <summary>
    /// New profile photo as base64-encoded JPEG or PNG bytes (a <c>data:image/…;base64,</c>
    /// prefix is tolerated), at most 5 MB decoded. Follows the <see cref="Gender"/> precedent on
    /// this otherwise full-replacement form: omitted or empty means "leave the stored photo
    /// alone" — a client saving a phone-number edit must not silently discard the photo it never
    /// re-sent. To remove the photo, send <see cref="RemovePhoto"/> instead.
    /// </summary>
    public string? PhotoBase64 { get; set; }

    /// <summary>
    /// True removes the stored photo (and deletes the underlying blob). Removal is an explicit
    /// action rather than "PhotoBase64 omitted" for the reason PhotoBase64's remarks give.
    /// Sending both is a client bug the validator rejects.
    /// </summary>
    public bool RemovePhoto { get; set; }

    public AlertSensitivity AlertSensitivity { get; set; } = AlertSensitivity.Medium;
}
