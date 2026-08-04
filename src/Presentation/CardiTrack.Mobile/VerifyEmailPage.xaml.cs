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
    private static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(45);

    private readonly IAuthService _authService;
    private readonly ICardiTrackApiClient _api;
    private readonly PostLoginRouter _router;
    private readonly string _email;
    private readonly string _password;

    private bool _hasAppeared;
    private bool _isChecking;
    private bool _resendOnCooldown;
    private CancellationTokenSource? _cooldownCts;

    public VerifyEmailPage(string email, string password)
    {
        InitializeComponent();
        _authService = ServiceHelper.GetRequiredService<IAuthService>();
        _api = ServiceHelper.GetRequiredService<ICardiTrackApiClient>();
        _router = ServiceHelper.GetRequiredService<PostLoginRouter>();
        _email = email;
        _password = password;

        EmailLabel.Text = email;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // First appearance: user just landed — wait for an explicit action.
        if (!_hasAppeared)
        {
            _hasAppeared = true;
            return;
        }

        // Returning from mail / background: silently re-check verification.
        await TryContinueAsync(silent: true);
    }

    protected override void OnDisappearing()
    {
        _cooldownCts?.Cancel();
        base.OnDisappearing();
    }

    private async void OnContinueClicked(object? sender, EventArgs e) =>
        await TryContinueAsync(silent: false);

    private async void OnOpenMailClicked(object? sender, EventArgs e)
    {
        try
        {
            await Launcher.Default.OpenAsync(new Uri($"mailto:{_email}"));
        }
        catch
        {
            ShowError("Couldn't open your mail app. Open it manually and check for our email.");
        }
    }

    private async void OnResendClicked(object? sender, EventArgs e)
    {
        if (_resendOnCooldown || !ResendBtn.IsEnabled)
            return;

        VerifyError.IsVisible = false;
        ResendStatus.IsVisible = false;
        ResendBtn.IsEnabled = false;
        ResendBtn.Text = "Sending...";

        try
        {
            await _api.ResendVerificationAsync(_email);
            ResendStatus.Text = "Sent — check your inbox";
            ResendStatus.IsVisible = true;
            await StartResendCooldownAsync();
        }
        catch (ApiException)
        {
            ResendBtn.Text = "Resend verification email";
            ResendBtn.IsEnabled = true;
            ShowError("Couldn't resend right now. Check your connection and try again.");
        }
    }

    private async void OnBackToSignInClicked(object? sender, EventArgs e)
    {
        WindowNavigation.SetRootPage(this, new NavigationPage(new SignInPage()));
        await Task.CompletedTask;
    }

    private async Task TryContinueAsync(bool silent)
    {
        if (_isChecking)
            return;

        _isChecking = true;
        VerifyError.IsVisible = false;
        SetCheckingUi(true);

        try
        {
            await _authService.SignInAsync(_email, _password);
            await _router.RouteAsync(this);
        }
        catch (AuthException ex)
        {
            if (silent && ex.Code == AuthErrorCode.EmailNotVerified)
                return;

            if (!silent || ex.Code == AuthErrorCode.Network)
            {
                ShowError(ex.Code switch
                {
                    AuthErrorCode.EmailNotVerified =>
                        "Not verified yet — open the link in your inbox (check spam too), then try again.",
                    AuthErrorCode.Network => "No connection. Check your internet and try again.",
                    _ => "Something went wrong. You can also go back and sign in.",
                });
            }
        }
        catch (ApiException)
        {
            if (!silent)
                ShowError("Verified, but we couldn't load your account. Check your connection and try again.");
        }
        finally
        {
            SetCheckingUi(false);
            _isChecking = false;
        }
    }

    private void SetCheckingUi(bool checking)
    {
        ContinueBtn.IsEnabled = !checking;
        OpenMailBtn.IsEnabled = !checking;
        ContinueBtn.Text = checking ? "Checking..." : "I've verified — continue";
        CheckingIndicator.IsVisible = checking;
        CheckingIndicator.IsRunning = checking;

        if (!_resendOnCooldown)
            ResendBtn.IsEnabled = !checking;
        BackBtn.IsEnabled = !checking;
    }

    private async Task StartResendCooldownAsync()
    {
        _cooldownCts?.Cancel();
        _cooldownCts = new CancellationTokenSource();
        var token = _cooldownCts.Token;

        _resendOnCooldown = true;
        ResendBtn.IsEnabled = false;
        ResendBtn.TextColor = (Color)App.Current!.Resources["MutedText"];

        try
        {
            var remaining = (int)ResendCooldown.TotalSeconds;
            while (remaining > 0)
            {
                ResendBtn.Text = $"Resend in {remaining}s";
                await Task.Delay(TimeSpan.FromSeconds(1), token);
                remaining--;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _resendOnCooldown = false;
        ResendBtn.Text = "Resend verification email";
        ResendBtn.TextColor = (Color)App.Current!.Resources["Primary"];
        ResendBtn.IsEnabled = !_isChecking;
        ResendStatus.IsVisible = false;
    }

    private void ShowError(string message)
    {
        VerifyError.Text = message;
        VerifyError.IsVisible = true;
    }
}
