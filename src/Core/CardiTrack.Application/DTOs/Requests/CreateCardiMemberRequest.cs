using System.ComponentModel.DataAnnotations;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.DTOs.Requests;

public class CreateCardiMemberRequest
{
    [Required(ErrorMessage = "Name is required")]
    [StringLength(100, MinimumLength = 2)]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "Date of birth is required")]
    public DateOnly DateOfBirth { get; set; }

    [Required(ErrorMessage = "Gender is required")]
    public Gender Gender { get; set; }

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
    /// Optional — see <see cref="UpdateCardiMemberRequest.RelationshipType"/>. Not knowing how you
    /// are related to someone is no reason to be unable to start watching over them.
    /// </summary>
    public RelationshipType RelationshipType { get; set; } = RelationshipType.Other;

    public bool IsPrimaryCaregiver { get; set; } = true;
}
