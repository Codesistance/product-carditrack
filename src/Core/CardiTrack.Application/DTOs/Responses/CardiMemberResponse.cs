using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.DTOs.Responses;

public class CardiMemberResponse
{
    public Guid Id { get; set; }

    /// <summary>
    /// The family that owns this member. The list this rides on is grant-scoped across every
    /// family the caller is in, so without it a client cannot tell whose member it is looking at —
    /// and two families watching one person hold two records under one name (D-15), so the name
    /// cannot stand in for it.
    /// </summary>
    public Guid OrganizationId { get; set; }

    /// <summary>What the app greets and labels this member by.</summary>
    public string FirstName { get; set; } = string.Empty;

    /// <summary>Surname, or null for someone known by a single name.</summary>
    public string? LastName { get; set; }

    /// <summary>
    /// Full name — <see cref="FirstName"/> and <see cref="LastName"/> joined. For the places a
    /// full name is needed; greetings and labels use <see cref="FirstName"/>. Kept under its
    /// original name so app builds from before the split keep rendering.
    /// </summary>
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
