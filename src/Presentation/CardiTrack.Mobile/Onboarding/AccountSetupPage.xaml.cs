using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Auth;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile.Onboarding;

/// <summary>
/// Creates the server-side organization and user behind a fresh sign-in, then hands over to
/// <see cref="PostLoginRouter"/>. The page asks nothing: an account created on the phone is
/// always <see cref="OrganizationType.Family"/> — a business runs to dozens of CardiMembers,
/// which is a web experience, not a phone one — and the family's name is derived from the
/// signed-in user. So all that is left is to run the call, and to offer a retry if it fails.
/// </summary>
public partial class AccountSetupPage : ContentPage
{
    private readonly ICardiTrackApiClient _api;
    private readonly IAuthService _authService;
    private readonly PostLoginRouter _router;

    private bool _running;

    public AccountSetupPage()
    {
        InitializeComponent();
        _api = ServiceHelper.GetRequiredService<ICardiTrackApiClient>();
        _authService = ServiceHelper.GetRequiredService<IAuthService>();
        _router = ServiceHelper.GetRequiredService<PostLoginRouter>();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = SetupAsync();
    }

    private void OnRetryClicked(object? sender, EventArgs e) => _ = SetupAsync();

    /// <summary>
    /// Safe to call more than once — a retry that reaches the server after a lost response
    /// gets the already-created account back rather than a second organization.
    /// </summary>
    private async Task SetupAsync()
    {
        if (_running)
            return;
        _running = true;

        SetupError.IsVisible = false;
        RetryBtn.IsVisible = false;
        SetupStatus.IsVisible = true;
        SetupSpinner.IsRunning = true;

        var orgName = FamilyOrgName();

        try
        {
            // One atomic call — organization and user are created in the same server
            // transaction, so a failure here never leaves a half-finished account.
            await _api.SetupAsync(new OnboardingSetupRequest
            {
                Organization = new CreateOrganizationRequest
                {
                    Name = orgName,
                    Type = OrganizationType.Family,
                },
                User = new OnboardingSetupUserRequest
                {
                    Email = _authService.CurrentUserEmail ?? string.Empty,
                    Name = _authService.CurrentUserName ?? orgName,
                    Role = UserRole.Member,
                    TimeZoneId = TimeZoneInfo.Local.Id,
                },
            });

            await _router.RouteAsync(this);
        }
        catch (ApiException ex)
        {
            SetupSpinner.IsRunning = false;
            SetupStatus.IsVisible = false;
            SetupError.Text = ex.Message;
            SetupError.IsVisible = true;
            RetryBtn.IsVisible = true;
        }
        finally
        {
            _running = false;
        }
    }

    private string FamilyOrgName()
    {
        var name = _authService.CurrentUserName;
        var firstName = string.IsNullOrWhiteSpace(name) ? null : name.Split(' ')[0];
        return firstName is null ? "My Family" : $"{firstName}'s Family";
    }
}
