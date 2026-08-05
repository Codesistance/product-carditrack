namespace CardiTrack.Application.DTOs.Responses;

public class OnboardingSetupResponse
{
    public OrganizationResponse Organization { get; set; } = new();
    public UserResponse User { get; set; } = new();
}
