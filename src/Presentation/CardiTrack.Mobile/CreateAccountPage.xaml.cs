using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Auth;
using CardiTrack.Mobile.Core.Configuration;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

public partial class CreateAccountPage : ContentPage
{
    private readonly BoxView[] _strengthBars;
    private readonly IAuthService _authService;
    private readonly IPopupService _popups;
    private readonly PostLoginRouter _router;
    private bool _socialBusy;

    public CreateAccountPage()
    {
        InitializeComponent();
        _strengthBars = [Str0, Str1, Str2, Str3];
        _authService = ServiceHelper.GetRequiredService<IAuthService>();
        _popups = ServiceHelper.GetRequiredService<IPopupService>();
        _router = ServiceHelper.GetRequiredService<PostLoginRouter>();
        UpdateCreateButtonState();
    }

    private async void OnGoogleTapped(object? sender, TappedEventArgs e)
        => await SocialSignInAsync(Auth0Options.GoogleConnection);

    private async void OnAppleTapped(object? sender, TappedEventArgs e)
        => await SocialSignInAsync(Auth0Options.AppleConnection);

    // The social buttons deliberately bypass the form and the terms checkbox: for a
    // provider account, "sign up" and "sign in" are the same operation, and consent is
    // gathered in the provider's own flow.
    private async Task SocialSignInAsync(string connection)
    {
        // Border gestures have no IsEnabled — an explicit guard stops double-taps
        // from opening two browser sheets.
        if (_socialBusy)
            return;
        _socialBusy = true;
        GoogleBtn.Opacity = AppleBtn.Opacity = 0.6;
        ErrorBanner.IsVisible = false;

        try
        {
            await _authService.SignInWithProviderAsync(connection);
            await _router.RouteAsync(this);
        }
        catch (TaskCanceledException)
        {
            // Browser sheet dismissed — back to the page, no error banner (Fitbit precedent).
        }
        catch (AuthException ex)
        {
            var (title, detail) = ex.Code switch
            {
                AuthErrorCode.ProviderUnavailable => ("Not available yet",
                    "That sign-in method isn't available yet. Use your email and password for now."),
                AuthErrorCode.EmailNotVerified => ("Verify your email",
                    "Check your inbox for the verification link, then try again."),
                AuthErrorCode.Network => ("No connection",
                    "Check your internet connection and try again."),
                AuthErrorCode.NotConfigured => ("Something went wrong",
                    "Sign-in isn't configured for this build."),
                _ => ("Something went wrong", "Sign in failed. Please try again."),
            };
            ShowErrorBanner(title, detail);
        }
        catch (ApiException)
        {
            ShowErrorBanner("Something went wrong",
                "Signed in, but we couldn't load your account. Check your connection and try again.");
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NotImplementedException)
        {
            ShowErrorBanner("Not available here",
                "Social sign-in uses the system browser and is available on iOS and Android.");
        }
        finally
        {
            _socialBusy = false;
            GoogleBtn.Opacity = AppleBtn.Opacity = 1;
        }
    }

    private void OnPasswordTextChanged(object? sender, TextChangedEventArgs e)
    {
        var strength = EvaluatePasswordStrength(e.NewTextValue ?? string.Empty);
        UpdateStrengthIndicator(strength);
        UpdateCreateButtonState();
    }

    private void OnFormFieldChanged(object? sender, TextChangedEventArgs e)
        => UpdateCreateButtonState();

    private void OnTermsCheckedChanged(object? sender, CheckedChangedEventArgs e)
        => UpdateCreateButtonState();

    private bool IsFormComplete()
        => !string.IsNullOrWhiteSpace(NameEntry.Text)
           && !string.IsNullOrWhiteSpace(EmailEntry.Text) && EmailEntry.Text.Contains('@')
           && !string.IsNullOrEmpty(PasswordEntry.Text) && PasswordEntry.Text.Length >= 8
           && ConfirmEntry.Text == PasswordEntry.Text
           && TermsCheck.IsChecked;

    private void UpdateCreateButtonState()
        => CreateBtn.Background = (Brush)App.Current!.Resources[
            IsFormComplete() ? "GradientButtonBrush" : "GradientButtonLightBrush"];

    private static int EvaluatePasswordStrength(string password)
    {
        if (string.IsNullOrEmpty(password)) return 0;
        var score = 0;
        if (password.Length >= 4) score++;
        if (password.Length >= 8) score++;
        if (password.Any(char.IsUpper) && password.Any(char.IsLower)) score++;
        if (password.Any(c => !char.IsLetterOrDigit(c))) score++;
        return score;
    }

    private void UpdateStrengthIndicator(int score)
    {
        var (color, label) = score switch
        {
            0 => ((Color)App.Current!.Resources["Divider"], ""),
            1 => ((Color)App.Current!.Resources["ErrorRed"], "Password strength: Weak"),
            2 => ((Color)App.Current!.Resources["Primary"], "Password strength: Medium"),
            3 => ((Color)App.Current!.Resources["Primary"], "Password strength: Strong"),
            _ => ((Color)App.Current!.Resources["StrengthStrong"], "Password strength: Strong"),
        };

        var emptyColor = (Color)App.Current!.Resources["Divider"];

        for (var i = 0; i < _strengthBars.Length; i++)
            _strengthBars[i].Color = i < score ? color : emptyColor;

        StrengthLabel.Text = label;
        StrengthLabel.TextColor = color;
    }

    private bool ValidateForm()
    {
        var valid = true;
        var errorBorder = (Color)App.Current!.Resources["ErrorRed"];
        var normalBorder = (Color)App.Current!.Resources["InputBorder"];

        if (string.IsNullOrWhiteSpace(NameEntry.Text))
        {
            NameBorder.Stroke = new SolidColorBrush(errorBorder);
            NameError.Text = "Name is required";
            NameError.IsVisible = true;
            valid = false;
        }
        else
        {
            NameBorder.Stroke = new SolidColorBrush(normalBorder);
            NameError.IsVisible = false;
        }

        if (string.IsNullOrWhiteSpace(EmailEntry.Text) || !EmailEntry.Text.Contains('@'))
        {
            EmailBorder.Stroke = new SolidColorBrush(errorBorder);
            EmailError.Text = "Valid email is required";
            EmailError.IsVisible = true;
            valid = false;
        }
        else
        {
            EmailBorder.Stroke = new SolidColorBrush(normalBorder);
            EmailError.IsVisible = false;
        }

        if (string.IsNullOrWhiteSpace(PasswordEntry.Text) || PasswordEntry.Text.Length < 8)
        {
            PasswordBorder.Stroke = new SolidColorBrush(errorBorder);
            valid = false;
        }
        else
        {
            PasswordBorder.Stroke = new SolidColorBrush(normalBorder);
        }

        if (ConfirmEntry.Text != PasswordEntry.Text)
        {
            ConfirmBorder.Stroke = new SolidColorBrush(errorBorder);
            ConfirmError.Text = "Password do not match";
            ConfirmError.IsVisible = true;
            valid = false;
        }
        else
        {
            ConfirmBorder.Stroke = new SolidColorBrush(normalBorder);
            ConfirmError.IsVisible = false;
        }

        return valid;
    }

    private async void OnCreateAccountClicked(object? sender, EventArgs e)
    {
        if (!ValidateForm())
            return;

        if (!TermsCheck.IsChecked)
        {
            await _popups.ShowWarningAsync(
                "Please agree to the Terms of Service and Privacy Policy to continue.", "One more thing");
            return;
        }

        SetLoadingState(true);
        ErrorBanner.IsVisible = false;
        EmailError.IsVisible = false;

        try
        {
            await _authService.SignUpAsync(NameEntry.Text.Trim(), EmailEntry.Text.Trim(), PasswordEntry.Text);
            // Sign-in is gated on email verification (tenant hard gate) — hand the
            // credentials to the verify page, which signs in once the link is clicked.
            await Navigation.PushAsync(new VerifyEmailPage(EmailEntry.Text.Trim(), PasswordEntry.Text));
        }
        catch (AuthException ex) when (ex.Code == AuthErrorCode.UserAlreadyExists)
        {
            EmailBorder.Stroke = new SolidColorBrush((Color)App.Current!.Resources["ErrorRed"]);
            EmailError.Text = "An account with this email already exists. Sign in instead.";
            EmailError.IsVisible = true;
        }
        catch (AuthException ex) when (ex.Code == AuthErrorCode.WeakPassword)
        {
            ShowErrorBanner("Password too weak",
                ex.Auth0Description ?? "Choose a longer password with a mix of letters, numbers, and symbols.");
        }
        catch (AuthException ex)
        {
            ShowErrorBanner(
                ex.Code == AuthErrorCode.Network ? "No connection" : "Something went wrong",
                ex.Code == AuthErrorCode.Network
                    ? "Check your internet connection and try again."
                    : "We couldn't create your account. Please try again.");
        }
        finally
        {
            SetLoadingState(false);
        }
    }

    private void ShowErrorBanner(string title, string detail)
    {
        ErrorBannerTitle.Text = title;
        ErrorBannerDetail.Text = detail;
        ErrorBanner.IsVisible = true;
    }

    private void SetLoadingState(bool loading)
    {
        CreateBtn.Text = loading ? "Create Account..." : "Create Account";
        CreateBtn.IsEnabled = !loading;
        NameEntry.IsEnabled = !loading;
        EmailEntry.IsEnabled = !loading;
        PasswordEntry.IsEnabled = !loading;
        ConfirmEntry.IsEnabled = !loading;
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

    private void OnConfirmToggleClicked(object? sender, EventArgs e)
    {
        ConfirmEntry.IsPassword = !ConfirmEntry.IsPassword;
        ConfirmToggle.Source = ConfirmEntry.IsPassword ? "icon_eye_off.svg" : "icon_eye.svg";
    }

    private async void OnSignInTapped(object? sender, EventArgs e)
    {
        await Navigation.PushAsync(new SignInPage());
    }
}