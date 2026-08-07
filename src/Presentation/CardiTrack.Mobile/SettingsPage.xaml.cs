using CardiTrack.Mobile.Core.Auth;
using CardiTrack.Mobile.Onboarding;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

public partial class SettingsPage : ContentPage
{
    private readonly IAuthService _authService;
    private readonly IPopupService _popups;

    public SettingsPage(IAuthService authService, IPopupService popups)
    {
        InitializeComponent();
        _authService = authService;
        _popups = popups;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        AccountNameLabel.Text = _authService.CurrentUserName ?? "Your account";
        AccountEmailLabel.Text = _authService.CurrentUserEmail ?? string.Empty;
    }

    private async void OnSignOutClicked(object? sender, EventArgs e)
    {
        var confirmed = await _popups.ConfirmWarningAsync(
            "You'll need to sign back in to keep an eye on your loved ones.",
            "Ready to sign out?", confirmText: "Sign out");
        if (!confirmed)
            return;

        SignOutBtn.IsEnabled = false;
        try
        {
            await _authService.SignOutAsync();
            Preferences.Default.Remove("PrimaryCardiMemberId");
            Preferences.Default.Remove("VerifyEmailNudgeDismissed");
            Preferences.Default.Remove(WizardLauncher.ResumeDismissedKey);
            // Holds a name, DOB and medical notes — must not survive into the next session.
            await CardiMemberDraft.ClearAsync();
            WindowNavigation.SetRootPage(this, new NavigationPage(new SignInPage()));
        }
        finally
        {
            SignOutBtn.IsEnabled = true;
        }
    }
}
