using System.ComponentModel.DataAnnotations;
using CardiTrack.Domain.Common;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.DTOs.Requests;

public class CreateCardiMemberRequest
{
    /// <summary>What the family calls this person — the name the app greets them by.</summary>
    /// <remarks>
    /// Falls back to the first token of the legacy <see cref="Name"/> only when the property is
    /// omitted, so an app build from before the split still creates and edits members exactly as
    /// it did. A first name that is sent — blank, or an explicit null — is validated as sent,
    /// never replaced.
    /// </remarks>
    [StringLength(100)]
    public string FirstName
    {
        get => _firstNameSent ? _firstName! : PersonName.Split(Name).FirstName;
        set
        {
            _firstName = value;
            _firstNameSent = true;
        }
    }

    /// <summary>Surname; null or empty for someone known by a single name.</summary>
    /// <remarks>
    /// When <see cref="FirstName"/> was omitted, this is the remainder of the legacy
    /// <see cref="Name"/> instead — the two parts always come from the same source, never one
    /// from each.
    /// </remarks>
    [StringLength(100)]
    public string? LastName
    {
        get => _firstNameSent ? _lastName : PersonName.Split(Name).LastName;
        set => _lastName = value;
    }

    /// <summary>
    /// Legacy single full name. App builds from before the split send only this; current builds
    /// send it too, restating the full name so they still work against an API from before the
    /// split. Ignored whenever <see cref="FirstName"/> is sent, even blank or null; otherwise split with the same rule
    /// the migration used on stored names.
    /// </summary>
    public string? Name { get; set; }

    private string? _firstName;

    // Whether FirstName was sent at all. The deserializer calls the setter for any property in
    // the payload, explicit null included, and never for an omitted one — the one distinction
    // the legacy fallback turns on.
    private bool _firstNameSent;
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
