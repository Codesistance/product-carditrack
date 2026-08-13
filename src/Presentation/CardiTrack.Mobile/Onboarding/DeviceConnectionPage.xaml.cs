using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Devices;
using CardiTrack.Mobile.Services;
using Microsoft.Extensions.Logging;

namespace CardiTrack.Mobile.Onboarding;

/// <summary>
/// M1-06: device permission explainer + the PKCE OAuth round-trip, brand-agnostic — the
/// <see cref="ConnectableDevice"/> passed in supplies the wire name and copy, and every brand on
/// the same data-source API shares this flow. The API issues the authorization URL and
/// state/verifier; the system browser handles provider login and redirects back to the app's
/// deep link, which is posted to the OAuth callback endpoint.
/// </summary>
public partial class DeviceConnectionPage : ContentPage
{
    public const string CallbackUri = "carditrack://oauth/callback";

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;
    private readonly ILogger<DeviceConnectionPage> _logger;
    private readonly WizardContext _ctx;
    private readonly CardiMemberResponse _member;
    private readonly ConnectableDevice _device;

    public DeviceConnectionPage(WizardContext ctx, ConnectableDevice device)
    {
        InitializeComponent();
        _api = ServiceHelper.GetRequiredService<ICardiTrackApiClient>();
        _popups = ServiceHelper.GetRequiredService<IPopupService>();
        _logger = ServiceHelper.GetRequiredService<ILogger<DeviceConnectionPage>>();
        _ctx = ctx;
        _member = ctx.RequireMember();
        _device = device;
        Header.Title = $"{device.DisplayName} Connection";
        TitleHeading.Text = $"Connect Your {device.DisplayName}";
        DeviceLogoText.Text = device.LogoText;
        AuthorizeBtn.Text = $"Authorize {device.DisplayName}";
        NeedsLabel.Text = $"To look after {_member.Name}, CardiTrack needs:";
        AuthorizingLabel.Text = $"Connecting to {_member.Name}'s {device.DisplayName}...";
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
                Provider = _device.WireName,
                RedirectUri = CallbackUri,
            });

            var authResult = await WebAuthenticator.Default.AuthenticateAsync(new WebAuthenticatorOptions
            {
                Url = new Uri(initiation.AuthorizationUrl),
                CallbackUrl = new Uri(CallbackUri),
            });

            // The state token is the CSRF binding, so it is checked before anything else is
            // trusted — including which error the callback claims to be reporting.
            authResult.Properties.TryGetValue("state", out var state);
            if (!string.Equals(state, initiation.State, StringComparison.Ordinal))
            {
                _logger.LogWarning("Device OAuth callback carried a state token we didn't issue.");
                ShowError();
                return;
            }

            // The bounce endpoint forwards a denied or failed authorization rather than ending
            // the response in the browser, so the app is the only place these are surfaced.
            if (authResult.Properties.TryGetValue("error", out var error) && !string.IsNullOrEmpty(error))
            {
                authResult.Properties.TryGetValue("error_description", out var description);
                _logger.LogInformation(
                    "Device OAuth was not granted: {Error} {Description}", error, description);
                ShowError(DescribeAuthorizationError(error));
                return;
            }

            if (!authResult.Properties.TryGetValue("code", out var code) || string.IsNullOrEmpty(code))
            {
                _logger.LogWarning("Device OAuth callback arrived without an authorization code.");
                ShowError();
                return;
            }

            var device = await _api.CompleteDeviceConnectionAsync(_device.WireName, new OAuthCallbackRequest
            {
                Code = code,
                State = state!,
                CodeVerifier = initiation.CodeVerifier,
            });

            _ctx.DeviceConnected = true;
            // A fresh connection means the post-login resume prompt is welcome again
            // if this device is ever disconnected later.
            Preferences.Default.Remove(WizardLauncher.ResumeDismissedKey);
            await ShowSuccessAsync(device);
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

    /// <summary>
    /// Swaps this page out for the confirmation. Left in the stack it stays one hardware-back
    /// press away with Authorize still live, which sends an already-connected member back
    /// through the provider's consent screen.
    /// </summary>
    private async Task ShowSuccessAsync(DeviceResponse device)
    {
        await Navigation.PushAsync(new ConnectionSuccessPage(_ctx, device));
        Navigation.RemovePage(this);
    }

    /// <summary>Provider error codes are OAuth wire values — say what they mean for this screen.</summary>
    private static string DescribeAuthorizationError(string error) => error switch
    {
        "access_denied" => "Connection cancelled — nothing was shared",
        "invalid_scope" => "That account can't share the data we need",
        _ => "We couldn't connect — let's try that again",
    };

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
