using System.ComponentModel.DataAnnotations;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.DTOs.Requests;

/// <summary>
/// Combined onboarding payload: organization and user are created together in one
/// transaction so a client failure can never leave an organization without a user.
/// </summary>
public class OnboardingSetupRequest
{
    [Required(ErrorMessage = "Organization details are required")]
    public CreateOrganizationRequest Organization { get; set; } = new();

    [Required(ErrorMessage = "User details are required")]
    public OnboardingSetupUserRequest User { get; set; } = new();
}

/// <summary>
/// User portion of <see cref="OnboardingSetupRequest"/>. Unlike
/// <see cref="CreateUserRequest"/> there is no OrganizationId — the server links the
/// user to the organization created in the same transaction — and Auth0UserId and
/// EmailVerified always come from the access token.
/// </summary>
public class OnboardingSetupUserRequest
{
    [Required(ErrorMessage = "Email is required")]
    [EmailAddress(ErrorMessage = "Invalid email format")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Name is required")]
    [StringLength(100, MinimumLength = 2)]
    public string Name { get; set; } = string.Empty;

    [Phone(ErrorMessage = "Invalid phone number")]
    public string? Phone { get; set; }

    public UserRole Role { get; set; } = UserRole.Member;

    [StringLength(10)]
    public string Locale { get; set; } = "en-US";

    [StringLength(50)]
    public string TimeZoneId { get; set; } = "UTC";
}
