using CardiTrack.Mobile.Core.Auth;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

public partial class SplashPage : ContentPage
{
    /// <summary>
    /// How long the logo stays on screen before the app moves on, so it doesn't flash away on
    /// fast paths. Measured from launch, not from this page appearing: the OS splash has been
    /// showing the same mark since process start, and counting that time from zero again would
    /// bill the user twice for the same reassurance.
    /// </summary>
    private static readonly TimeSpan MinimumBrandHold = TimeSpan.FromMilliseconds(900);

    /// <summary>Cross-fade from the OS splash's flat colour to the gradient.</summary>
    private const uint BackgroundFadeMs = 250;

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
        _ = GradientBackground.FadeToAsync(1, BackgroundFadeMs, Easing.CubicOut);
        _ = RunStartupAsync(MinimumBrandHold - AppStartup.Elapsed);
    }

    private async void OnRetryClicked(object? sender, EventArgs e)
    {
        LoadingPanel.IsVisible = true;
        ErrorPanel.IsVisible = false;
        // No OS splash precedes a retry, so its hold runs from the tap.
        await RunStartupAsync(MinimumBrandHold);
    }

    private async Task RunStartupAsync(TimeSpan minimumHold)
    {
        var minimumSplash = minimumHold > TimeSpan.Zero ? Task.Delay(minimumHold) : Task.CompletedTask;

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
            // Signed out — first-run, or the refresh token was rejected. A network miss
            // during refresh keeps the stored session so this branch is not the offline path.
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
