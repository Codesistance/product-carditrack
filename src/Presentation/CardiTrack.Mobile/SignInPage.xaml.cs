using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Auth;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

public partial class SignInPage : ContentPage
{
    private readonly IAuthService _authService;
    private readonly PostLoginRouter _router;

    public SignInPage()
    {
        InitializeComponent();
        _authService = ServiceHelper.GetRequiredService<IAuthService>();
        _router = ServiceHelper.GetRequiredService<PostLoginRouter>();
        UpdateSignInButtonState();
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
