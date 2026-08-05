using CardiTrack.Domain.Common;
using CardiTrack.Domain.Enums;
using CardiTrack.Domain.Interfaces;

namespace CardiTrack.Domain.Entities;

public class User : BaseEntity, ISoftDeletable
{
    public Guid OrganizationId { get; set; }
    public string Auth0UserId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public UserRole Role { get; set; } = UserRole.Member;
    public bool EmailVerified { get; set; }
    public DateTime? LastLoginDate { get; set; }
    public bool IsActive { get; set; } = true;

    // UTC timestamp of when the user dismissed the Google-required health-data
    // disclosure banner; null means it must still be shown
    public DateTime? HealthDataDisclosureDismissedDate { get; set; }

    // Locale preferences
    public string Locale { get; set; } = "en-US";
    public string TimeZoneId { get; set; } = "UTC";

    // Navigation properties
    public ICollection<UserCardiMember> UserCardiMembers { get; set; } = new List<UserCardiMember>();
}
