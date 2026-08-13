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

    public AlertSensitivity AlertSensitivity { get; set; } = AlertSensitivity.Medium;
}
