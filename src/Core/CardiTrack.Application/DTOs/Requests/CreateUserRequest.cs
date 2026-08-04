using System.ComponentModel.DataAnnotations;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.DTOs.Requests;

public class CreateUserRequest
{
    public string Auth0UserId { get; set; } = string.Empty; // Set by middleware

    /// <summary>
    /// Set by the API from the access token's email_verified claim; any client-sent
    /// value is overwritten. Null when the claim is absent from the token.
    /// </summary>
    public bool? EmailVerified { get; set; }

    [Required(ErrorMessage = "Email is required")]
    [EmailAddress(ErrorMessage = "Invalid email format")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Name is required")]
    [StringLength(100, MinimumLength = 2)]
    public string Name { get; set; } = string.Empty;

    [Phone(ErrorMessage = "Invalid phone number")]
    public string? Phone { get; set; }

    [Required(ErrorMessage = "Organization ID is required")]
    public Guid OrganizationId { get; set; }

    public UserRole Role { get; set; } = UserRole.Member;

    [StringLength(10)]
    public string Locale { get; set; } = "en-US";

    [StringLength(50)]
    public string TimeZoneId { get; set; } = "UTC";
}
