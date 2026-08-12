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
