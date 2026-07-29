namespace CardiTrack.Mobile;

public partial class SignInPage : ContentPage
{
    public SignInPage()
    {
        InitializeComponent();
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

        SignInBtn.Text = "Signing in...";
        SignInBtn.IsEnabled = false;
        EmailEntry.IsEnabled = false;
        PasswordEntry.IsEnabled = false;

        try
        {
            await Task.Delay(1200);

            await MainThread.InvokeOnMainThreadAsync(() =>
                WindowNavigation.SetRootPage(this, new AppShell()));
        }
        catch
        {
            await DisplayAlertAsync("Error", "Sign in failed. Please try again.", "OK");
        }
        finally
        {
            SignInBtn.Text = "Sign in";
            SignInBtn.IsEnabled = true;
            EmailEntry.IsEnabled = true;
            PasswordEntry.IsEnabled = true;
        }
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
        await Navigation.PushAsync(new ForgotPasswordPage());
    }

    private async void OnSignUpTapped(object? sender, EventArgs e)
    {
        if (Navigation.NavigationStack.Count > 1)
            await Navigation.PopAsync();
        else
            await Navigation.PushAsync(new CreateAccountPage());
    }
}