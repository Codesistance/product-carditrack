using CardiTrack.Mobile.Core.Auth;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

public partial class ForgotPasswordPage : ContentPage
{
    private static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(30);

    private readonly IAuthService _authService;

    public ForgotPasswordPage(string? email = null)
    {
        InitializeComponent();
        _authService = ServiceHelper.GetRequiredService<IAuthService>();
        if (!string.IsNullOrWhiteSpace(email))
            EmailEntry.Text = email.Trim();
        UpdateSendButtonState();
    }

    private void OnEmailChanged(object? sender, TextChangedEventArgs e)
        => UpdateSendButtonState();

    private void UpdateSendButtonState()
    {
        var complete = !string.IsNullOrWhiteSpace(EmailEntry.Text) && EmailEntry.Text.Contains('@');
        SendBtn.Background = (Brush)App.Current!.Resources[
            complete ? "GradientButtonBrush" : "GradientButtonLightBrush"];
    }

    private async void OnSendClicked(object? sender, EventArgs e) =>
        await SendResetLinkAsync(fromResend: false);

    private async void OnResendTapped(object? sender, EventArgs e)
    {
        if (!ResendLink.IsEnabled)
            return;
        await SendResetLinkAsync(fromResend: true);
    }

    private async Task SendResetLinkAsync(bool fromResend)
    {
        var email = EmailEntry.Text?.Trim();
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            EmailBorder.Stroke = new SolidColorBrush((Color)App.Current!.Resources["ErrorRed"]);
            ResetError.Text = "Enter a valid email address";
            ResetError.IsVisible = true;
            return;
        }
        ResetError.IsVisible = false;

        SendBtn.Text = "Sending...";
        SendBtn.IsEnabled = false;

        try
        {
            await _authService.RequestPasswordResetAsync(email);

            ConfirmationDetail.Text = $"We sent a password reset link to {email}";
            RequestPanel.IsVisible = false;
            ConfirmationPanel.IsVisible = true;
            _ = StartResendCooldownAsync();
        }
        catch (AuthException ex)
        {
            var message = ex.Code switch
            {
                AuthErrorCode.Network => "No connection. Check your internet and try again.",
                AuthErrorCode.NotConfigured => "Password reset isn't configured for this build.",
                _ => "We couldn't send the reset link. Please try again.",
            };
            if (fromResend)
                await DisplayAlertAsync("Error", message, "OK");
            else
            {
                ResetError.Text = message;
                ResetError.IsVisible = true;
            }
        }
        finally
        {
            SendBtn.Text = "Send reset link";
            SendBtn.IsEnabled = true;
        }
    }

    private async Task StartResendCooldownAsync()
    {
        ResendLink.IsEnabled = false;
        ResendLink.TextColor = (Color)App.Current!.Resources["MutedText"];
        await Task.Delay(ResendCooldown);
        ResendLink.IsEnabled = true;
        ResendLink.TextColor = (Color)App.Current!.Resources["Primary"];
    }

    private async void OnBackToSignInTapped(object? sender, EventArgs e)
    {
        if (Navigation.NavigationStack.Count > 1)
            await Navigation.PopAsync();
        else
            WindowNavigation.SetRootPage(this, new NavigationPage(new SignInPage()));
    }

    private void OnEntryFocused(object? sender, FocusEventArgs e)
    {
        if (sender is Entry entry && entry.Parent is Border border)
            border.Stroke = new SolidColorBrush((Color)App.Current!.Resources["InputFocusBorder"]);
    }

    private void OnEntryUnfocused(object? sender, FocusEventArgs e)
    {
        if (sender is Entry entry && entry.Parent is Border border)
            border.Stroke = new SolidColorBrush((Color)App.Current!.Resources["InputBorder"]);
    }
}
