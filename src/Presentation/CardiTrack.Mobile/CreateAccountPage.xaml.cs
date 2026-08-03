using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Auth;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

public partial class CreateAccountPage : ContentPage
{
    private readonly BoxView[] _strengthBars;
    private readonly IAuthService _authService;
    private readonly PostLoginRouter _router;

    public CreateAccountPage()
    {
        InitializeComponent();
        _strengthBars = [Str0, Str1, Str2, Str3];
        _authService = ServiceHelper.GetRequiredService<IAuthService>();
        _router = ServiceHelper.GetRequiredService<PostLoginRouter>();
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
            await DisplayAlertAsync("Terms Required", "Please agree to the Terms of Service and Privacy Policy.", "OK");
            return;
        }

        SetLoadingState(true);
        ErrorBanner.IsVisible = false;
        EmailError.IsVisible = false;

        try
        {
            await _authService.SignUpAsync(NameEntry.Text.Trim(), EmailEntry.Text.Trim(), PasswordEntry.Text);
            await _router.RouteAsync(this);
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
        catch (ApiException)
        {
            // Auth0 account exists and the user is signed in; only the status call failed.
            ShowErrorBanner("Account created, but we couldn't load your profile",
                "Check your connection and try again — you can also sign in later.");
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