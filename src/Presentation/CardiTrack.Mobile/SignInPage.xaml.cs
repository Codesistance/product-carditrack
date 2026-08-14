using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Auth;
using CardiTrack.Mobile.Core.Configuration;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

public partial class SignInPage : ContentPage
{
    /// <summary>Shown when the app returns here on its own after the session ended.</summary>
    public const string SessionExpiredNotice = "Your session expired — please sign in again.";

    private readonly IAuthService _authService;
    private readonly PostLoginRouter _router;

    /// <param name="notice">
    /// Why the user is looking at this page, when they didn't ask to be. Without it an
    /// expired session drops them on a bare sign-in form with no explanation.
    /// </param>
    public SignInPage(string? notice = null)
    {
        InitializeComponent();
        _authService = ServiceHelper.GetRequiredService<IAuthService>();
        _router = ServiceHelper.GetRequiredService<PostLoginRouter>();
        UpdateSignInButtonState();

        if (!string.IsNullOrEmpty(notice))
            ShowError(notice);
    }

    private async void OnSignInClicked(object? sender, EventArgs e)
    {
        var errorBorder = (Color)App.Current!.Resources["ErrorRed"];
        var normalBorder = (Color)App.Current!.Resources["InputBorder"];
        var valid = true;

        if (string.IsNullOrWhiteSpace(EmailEntry.Text) || !EmailEntry.Text.Contains('@'))
        {
            EmailBorder.Stroke = new SolidColorBrush(errorBorder);
            valid = false;
        }
        else
        {
            EmailBorder.Stroke = new SolidColorBrush(normalBorder);
        }

        if (string.IsNullOrWhiteSpace(PasswordEntry.Text))
        {
            PasswordBorder.Stroke = new SolidColorBrush(errorBorder);
            valid = false;
        }
        else
        {
            PasswordBorder.Stroke = new SolidColorBrush(normalBorder);
        }

        if (!valid)
            return;

        SignInError.IsVisible = false;
        SignInBtn.Text = "Signing in...";
        SignInBtn.IsEnabled = false;
        EmailEntry.IsEnabled = false;
        PasswordEntry.IsEnabled = false;

        try
        {
            await _authService.SignInAsync(EmailEntry.Text.Trim(), PasswordEntry.Text);
            await _router.RouteAsync(this);
        }
        catch (AuthException ex) when (ex.Code == AuthErrorCode.EmailNotVerified)
        {
            // Hand off to the verify page with these credentials so the user can
            // resend the link and continue without retyping anything.
            await Navigation.PushAsync(new VerifyEmailPage(EmailEntry.Text.Trim(), PasswordEntry.Text));
        }
        catch (AuthException ex)
        {
            ShowError(ex.Code switch
            {
                AuthErrorCode.InvalidCredentials => "Wrong email or password.",
                AuthErrorCode.TooManyAttempts => "Too many attempts. Try again later or reset your password.",
                AuthErrorCode.Network => "No connection. Check your internet and try again.",
                AuthErrorCode.NotConfigured => "Sign-in isn't configured for this build.",
                _ => "Sign in failed. Please try again.",
            });
        }
        catch (ApiException)
        {
            ShowError("Signed in, but we couldn't load your account. Check your connection and try again.");
        }
        finally
        {
            SignInBtn.Text = "Sign in";
            SignInBtn.IsEnabled = true;
            EmailEntry.IsEnabled = true;
            PasswordEntry.IsEnabled = true;
        }
    }

    private async void OnGoogleTapped(object? sender, TappedEventArgs e)
        => await SocialSignInAsync(Auth0Options.GoogleConnection);

    private async void OnAppleTapped(object? sender, TappedEventArgs e)
        => await SocialSignInAsync(Auth0Options.AppleConnection);

    private bool _socialBusy;

    private async Task SocialSignInAsync(string connection)
    {
        // Border gestures have no IsEnabled — an explicit guard stops double-taps
        // from opening two browser sheets.
        if (_socialBusy)
            return;
        _socialBusy = true;
        GoogleBtn.Opacity = AppleBtn.Opacity = 0.6;
        SignInError.IsVisible = false;

        try
        {
            await _authService.SignInWithProviderAsync(connection);
            await _router.RouteAsync(this);
        }
        catch (OperationCanceledException)
        {
            // Browser sheet dismissed — back to the page, no error banner (Fitbit precedent).
            // Covers both WebAuthenticator's own TaskCanceledException and the resume-triggered
            // cancellation WebBrowserAuthenticator raises when Android dismisses the Custom Tab
            // without a callback.
        }
        catch (AuthException ex)
        {
            ShowError(ex.Code switch
            {
                AuthErrorCode.ProviderUnavailable => "That sign-in method isn't available yet. Use your email and password for now.",
                AuthErrorCode.EmailNotVerified => "Verify your email to continue — check your inbox for the link.",
                AuthErrorCode.Network => "No connection. Check your internet and try again.",
                AuthErrorCode.NotConfigured => "Sign-in isn't configured for this build.",
                _ => "Sign in failed. Please try again.",
            });
        }
        catch (ApiException)
        {
            ShowError("Signed in, but we couldn't load your account. Check your connection and try again.");
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NotImplementedException)
        {
            ShowError("Social sign-in uses the system browser and is available on iOS and Android.");
        }
        finally
        {
            _socialBusy = false;
            GoogleBtn.Opacity = AppleBtn.Opacity = 1;
        }
    }

    private void OnCredentialsChanged(object? sender, TextChangedEventArgs e)
        => UpdateSignInButtonState();

    private void UpdateSignInButtonState()
    {
        var complete = !string.IsNullOrWhiteSpace(EmailEntry.Text) && EmailEntry.Text.Contains('@')
                       && !string.IsNullOrWhiteSpace(PasswordEntry.Text);
        SignInBtn.Background = (Brush)App.Current!.Resources[
            complete ? "GradientButtonBrush" : "GradientButtonLightBrush"];
    }

    private void ShowError(string message)
    {
        SignInError.Text = message;
        SignInError.IsVisible = true;
    }

    private void OnEntryFocused(object? sender, FocusEventArgs e)
    {
        if (sender is Entry entry && entry.Parent is Border border)
            border.Stroke = new SolidColorBrush((Color)App.Current!.Resources["InputFocusBorder"]);
        else if (sender is Entry entry2 && entry2.Parent is Grid grid && grid.Parent is Border parentBorder)
            parentBorder.Stroke = new SolidColorBrush((Color)App.Current!.Resources["InputFocusBorder"]);
    }

    private void OnEntryUnfocused(object? sender, FocusEventArgs e)
    {
        if (sender is Entry entry && entry.Parent is Border border)
            border.Stroke = new SolidColorBrush((Color)App.Current!.Resources["InputBorder"]);
        else if (sender is Entry entry2 && entry2.Parent is Grid grid && grid.Parent is Border parentBorder)
            parentBorder.Stroke = new SolidColorBrush((Color)App.Current!.Resources["InputBorder"]);
    }

    private void OnPasswordToggleClicked(object? sender, EventArgs e)
    {
        PasswordEntry.IsPassword = !PasswordEntry.IsPassword;
        PasswordToggle.Source = PasswordEntry.IsPassword ? "icon_eye_off.svg" : "icon_eye.svg";
    }

    private async void OnForgotPasswordTapped(object? sender, EventArgs e)
    {
        await Navigation.PushAsync(new ForgotPasswordPage(EmailEntry.Text));
    }

    private async void OnSignUpTapped(object? sender, EventArgs e)
    {
        if (Navigation.NavigationStack.Count > 1)
            await Navigation.PopAsync();
        else
            await Navigation.PushAsync(new CreateAccountPage());
    }
}
