using CardiTrack.Mobile.Core.Auth;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

public partial class SplashPage : ContentPage
{
    private readonly IAuthService _authService;
    private readonly PostLoginRouter _router;

    private bool _scheduledInitialStartup;

    public SplashPage()
    {
        InitializeComponent();
        _authService = ServiceHelper.GetRequiredService<IAuthService>();
        _router = ServiceHelper.GetRequiredService<PostLoginRouter>();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_scheduledInitialStartup)
            return;
        _scheduledInitialStartup = true;
        _ = RunStartupAsync();
    }

    private async void OnRetryClicked(object? sender, EventArgs e)
    {
        LoadingPanel.IsVisible = true;
        ErrorPanel.IsVisible = false;
        await RunStartupAsync();
    }

    private async Task RunStartupAsync()
    {
        // Brief hold so the logo doesn't flash away on fast paths.
        var minimumSplash = Task.Delay(900);

        bool hasSession;
        try
        {
            hasSession = await _authService.TrySilentSignInAsync();
        }
        catch
        {
            hasSession = false;
        }
        await minimumSplash;

        if (!hasSession)
        {
            // Signed out (or the session couldn't be restored offline) — normal first-run path.
            await MainThread.InvokeOnMainThreadAsync(() =>
                WindowNavigation.SetRootPage(this, new WelcomePage()));
            return;
        }

        try
        {
            await _router.RouteAsync(this);
        }
        catch
        {
            // Session is fine but the onboarding-status call failed — offer retry.
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                LoadingPanel.IsVisible = false;
                ErrorPanel.IsVisible = true;
            });
        }
    }
}
