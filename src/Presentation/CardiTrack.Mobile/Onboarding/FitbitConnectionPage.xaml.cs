using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile.Onboarding;

/// <summary>
/// M1-06: Fitbit permission explainer + the PKCE OAuth round-trip. The API issues the
/// authorization URL and state/verifier; the system browser handles Fitbit login and
/// redirects back to the app's deep link, which is posted to the OAuth callback endpoint.
/// </summary>
public partial class FitbitConnectionPage : ContentPage
{
    public const string CallbackUri = "carditrack://oauth/callback";
    private const string Provider = "fitbit";

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;
    private readonly CardiMemberResponse _member;

    public FitbitConnectionPage(CardiMemberResponse member)
    {
        InitializeComponent();
        _api = ServiceHelper.GetRequiredService<ICardiTrackApiClient>();
        _popups = ServiceHelper.GetRequiredService<IPopupService>();
        _member = member;
        NeedsLabel.Text = $"To look after {member.Name}, CardiTrack needs:";
        AuthorizingLabel.Text = $"Connecting to {member.Name}'s Fitbit...";
    }

    private async void OnAuthorizeClicked(object? sender, EventArgs e)
    {
        ConnectError.IsVisible = false;
        AuthorizingOverlay.IsVisible = true;
        AuthorizeBtn.IsEnabled = false;

        try
        {
            var initiation = await _api.InitiateDeviceConnectionAsync(_member.Id, new ConnectDeviceRequest
            {
                Provider = Provider,
                RedirectUri = CallbackUri,
            });

            var authResult = await WebAuthenticator.Default.AuthenticateAsync(new WebAuthenticatorOptions
            {
                Url = new Uri(initiation.AuthorizationUrl),
                CallbackUrl = new Uri(CallbackUri),
            });

            if (!authResult.Properties.TryGetValue("code", out var code) ||
                !authResult.Properties.TryGetValue("state", out var state) ||
                string.IsNullOrEmpty(code) || state != initiation.State)
            {
                ShowError();
                return;
            }

            var device = await _api.CompleteDeviceConnectionAsync(Provider, new OAuthCallbackRequest
            {
                Code = code,
                State = state,
                CodeVerifier = initiation.CodeVerifier,
            });

            await Navigation.PushAsync(new ConnectionSuccessPage(_member, device));
        }
        catch (TaskCanceledException)
        {
            // User closed the browser sheet — back to the default state, no error banner.
        }
        catch (ApiException ex)
        {
            ShowError(ex.Message);
        }
        catch (PlatformNotSupportedException)
        {
            await _popups.ShowWarningAsync(
                "Device connection uses the system browser and is available on iOS and Android.",
                "Not supported here");
        }
        catch (NotImplementedException)
        {
            await _popups.ShowWarningAsync(
                "Device connection uses the system browser and is available on iOS and Android.",
                "Not supported here");
        }
        finally
        {
            AuthorizingOverlay.IsVisible = false;
            AuthorizeBtn.IsEnabled = true;
        }
    }

    private void ShowError(string? message = null)
    {
        ConnectError.Text = message ?? "We couldn't connect — let's try that again";
        ConnectError.IsVisible = true;
        AuthorizeBtn.Text = "Try Again";
    }

    private async void OnCancelTapped(object? sender, EventArgs e)
    {
        if (Navigation.NavigationStack.Count > 1)
            await Navigation.PopAsync();
    }

    private Task OnInfoAsync(string title, string message) => _popups.ShowInfoAsync(message, title);

    private async void OnHeartInfoTapped(object? sender, EventArgs e) =>
        await OnInfoAsync("Heart Rate Data", "So we can spot if something's off");

    private async void OnActivityInfoTapped(object? sender, EventArgs e) =>
        await OnInfoAsync("Activity & Steps", "To make sure they're staying active");

    private async void OnSleepInfoTapped(object? sender, EventArgs e) =>
        await OnInfoAsync("Sleep Data", "To know they're resting well");
}
