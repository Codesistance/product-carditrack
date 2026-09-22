using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.DTOs.Responses;

public class UserResponse
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public UserRole Role { get; set; }
    public Guid? OrganizationId { get; set; }
    public bool IsActive { get; set; }
    public string Locale { get; set; } = string.Empty;
    public string TimeZoneId { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; }
}
