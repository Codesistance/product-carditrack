using System.ComponentModel.DataAnnotations;

namespace CardiTrack.Application.DTOs.Requests;

/// <summary>
/// POST /api/v1/auth/resend-verification — anonymous by design (the caller cannot log in
/// until verified). The endpoint always answers success to avoid user enumeration.
/// </summary>
public class ResendVerificationRequest
{
    [Required(ErrorMessage = "Email is required")]
    [EmailAddress(ErrorMessage = "Invalid email format")]
    public string Email { get; set; } = string.Empty;
}
