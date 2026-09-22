using System.ComponentModel.DataAnnotations;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.DTOs.Requests;

/// <summary>
/// Combined onboarding payload: organization and user are created together in one
/// transaction so a client failure can never leave an organization without a user.
/// </summary>
/// <remarks>
/// <see cref="Organization"/> is optional, and its absence is the whole of the second onboarding
/// path. Somebody starting a family sends one and gets an organization, a trial and an Admin
/// membership. Somebody joining a family someone else runs sends none and becomes a
/// <em>guest</em>: an account with no organization and no subscription, who gets both the moment
/// they first add a CardiMember of their own. Provisioning a family for them at signup would burn
/// a thirty-day trial nobody asked for and make "only the admin pays" a sentence with a hole in
/// it.
/// </remarks>
public class OnboardingSetupRequest
{
    /// <summary>
    /// The family to start, or null to sign up as a guest — see the remarks on this class.
    /// </summary>
    public CreateOrganizationRequest? Organization { get; set; }

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

    // No Role. It was here, and the server wrote it straight to User.Role — the same shape the
    // two-call onboarding path was deleted for. Nothing authorizes on that column any more (the
    // role that means something lives on UserOrganization), but a client-settable field named
    // "Role" is a trap for whoever next reads it as one. The server assigns it from what the
    // request actually does: Admin when it starts a family, Member when it joins as a guest.
    // A client still sending the field is ignored by deserialization rather than refused.

    [StringLength(10)]
    public string Locale { get; set; } = "en-US";

    [StringLength(50)]
    public string TimeZoneId { get; set; } = "UTC";
}
