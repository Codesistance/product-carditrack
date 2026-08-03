using CardiTrack.Mobile.Core.Auth;

namespace CardiTrack.Mobile;

public partial class SettingsPage : ContentPage
{
    private readonly IAuthService _authService;

    public SettingsPage(IAuthService authService)
    {
        InitializeComponent();
        _authService = authService;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        AccountNameLabel.Text = _authService.CurrentUserName ?? "Your account";
        AccountEmailLabel.Text = _authService.CurrentUserEmail ?? string.Empty;
    }

    private async void OnSignOutClicked(object? sender, EventArgs e)
    {
        var confirmed = await DisplayAlertAsync("Sign out", "Sign out of CardiTrack?", "Sign out", "Cancel");
        if (!confirmed)
            return;

        SignOutBtn.IsEnabled = false;
        try
        {
            await _authService.SignOutAsync();
            Preferences.Default.Remove("PrimaryCardiMemberId");
            WindowNavigation.SetRootPage(this, new NavigationPage(new SignInPage()));
        }
        finally
        {
            SignOutBtn.IsEnabled = true;
        }
    }
}
