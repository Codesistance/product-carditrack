using System.ComponentModel.DataAnnotations;
using CardiTrack.Domain.Common;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.DTOs.Requests;

public class CreateCardiMemberRequest
{
    /// <summary>What the family calls this person — the name the app greets them by.</summary>
    /// <remarks>
    /// Falls back to the first token of the legacy <see cref="Name"/> when not sent, so an app
    /// build from before the split still creates and edits members exactly as it did.
    /// </remarks>
    [StringLength(100)]
    public string FirstName
    {
        get => string.IsNullOrWhiteSpace(_firstName) ? PersonName.Split(Name).FirstName : _firstName;
        set => _firstName = value;
    }

    /// <summary>Surname; null or empty for someone known by a single name.</summary>
    /// <remarks>
    /// When <see cref="FirstName"/> was not sent, this is the remainder of the legacy
    /// <see cref="Name"/> instead — the two parts always come from the same source, never one
    /// from each.
    /// </remarks>
    [StringLength(100)]
    public string? LastName
    {
        get => string.IsNullOrWhiteSpace(_firstName) ? PersonName.Split(Name).LastName : _lastName;
        set => _lastName = value;
    }

    /// <summary>
    /// Legacy single full name. App builds from before the split send only this; current builds
    /// send it too, restating the full name so they still work against an API from before the
    /// split. Ignored whenever <see cref="FirstName"/> is sent; otherwise split with the same rule
    /// the migration used on stored names.
    /// </summary>
    public string? Name { get; set; }

    private string? _firstName;
    private string? _lastName;

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
    /// Optional profile photo as base64-encoded JPEG or PNG bytes (a <c>data:image/…;base64,</c>
    /// prefix is tolerated), at most 5 MB decoded. Validated, downscaled, and re-encoded with all
    /// metadata stripped before storage; if the photo is unusable the member is not created —
    /// half-saving the form would leave the caregiver unsure what was kept.
    /// </summary>
    public string? PhotoBase64 { get; set; }

    /// <summary>
    /// Optional — see <see cref="UpdateCardiMemberRequest.RelationshipType"/>. Not knowing how you
    /// are related to someone is no reason to be unable to start watching over them.
    /// </summary>
    public RelationshipType RelationshipType { get; set; } = RelationshipType.Other;

    public bool IsPrimaryCaregiver { get; set; } = true;
}
