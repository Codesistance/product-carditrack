using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.DTOs.Responses;

public class CardiMemberResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateOnly DateOfBirth { get; set; }
    public int Age { get; set; }
    public Gender Gender { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public RelationshipType Relationship { get; set; }
    public bool IsPrimaryCaregiver { get; set; }

    /// <summary>
    /// Short-lived signed URL for the member's profile photo, or null when none is set or photo
    /// storage is unavailable — clients fall back to an initials avatar. Expires within minutes:
    /// fetch it, don't store it.
    /// </summary>
    public string? PhotoUrl { get; set; }

    public bool IsActive { get; set; }
    public DateTime CreatedDate { get; set; }
}
