using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Auth;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

/// <summary>
/// Post-signup email-verification gate: the tenant denies sign-in until the user clicks
/// Auth0's link, so this page holds the credentials transiently, retries sign-in on
/// "continue", and offers a resend via the API's anonymous endpoint. Reached from
/// CreateAccountPage (after signup) and SignInPage (unverified account signing in).
/// </summary>
public partial class VerifyEmailPage : ContentPage
{
    private readonly IAuthService _authService;
    private readonly ICardiTrackApiClient _api;
    private readonly PostLoginRouter _router;
    private readonly string _email;
    private readonly string _password;

    public VerifyEmailPage(string email, string password)
    {
        InitializeComponent();
        _authService = ServiceHelper.GetRequiredService<IAuthService>();
        _api = ServiceHelper.GetRequiredService<ICardiTrackApiClient>();
        _router = ServiceHelper.GetRequiredService<PostLoginRouter>();
        _email = email;
        _password = password;

        DetailLabel.Text = $"We sent a verification link to {email}. Open it, then come back and continue.";
    }

    private async void OnContinueClicked(object? sender, EventArgs e)
    {
        VerifyError.IsVisible = false;
        ContinueBtn.Text = "Checking...";
        ContinueBtn.IsEnabled = false;

        try
        {
            await _authService.SignInAsync(_email, _password);
            await _router.RouteAsync(this);
        }
        catch (AuthException ex)
        {
            ShowError(ex.Code switch
            {
                AuthErrorCode.EmailNotVerified =>
                    "Not verified yet — open the link in your inbox (check spam too), then try again.",
                AuthErrorCode.Network => "No connection. Check your internet and try again.",
                _ => "Something went wrong. You can also go back and sign in.",
            });
        }
        catch (ApiException)
        {
            ShowError("Verified, but we couldn't load your account. Check your connection and try again.");
        }
        finally
        {
            ContinueBtn.Text = "I've verified — continue";
            ContinueBtn.IsEnabled = true;
        }
    }

    private async void OnResendTapped(object? sender, EventArgs e)
    {
        VerifyError.IsVisible = false;
        ResendLink.IsEnabled = false;
        ResendLink.Text = "Sending...";

        try
        {
            await _api.ResendVerificationAsync(_email);
            ResendLink.Text = "Sent — check your inbox";
        }
        catch (ApiException)
        {
            ResendLink.Text = "Resend verification email";
            ShowError("Couldn't resend right now. Check your connection and try again.");
        }
        finally
        {
            ResendLink.IsEnabled = true;
        }
    }

    private async void OnBackToSignInTapped(object? sender, EventArgs e)
    {
        WindowNavigation.SetRootPage(this, new NavigationPage(new SignInPage()));
        await Task.CompletedTask;
    }

    private void ShowError(string message)
    {
        VerifyError.Text = message;
        VerifyError.IsVisible = true;
    }
}
