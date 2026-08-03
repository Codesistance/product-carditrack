using CardiTrack.Mobile.Core.Auth;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

public partial class App : Microsoft.Maui.Controls.Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        // A rejected refresh token mid-session sends the user back to sign-in. Splash and
        // the auth pages handle their own failures, so only an authenticated root reacts.
        ServiceHelper.GetRequiredService<ITokenRefresher>().SessionExpired += () =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var window = Windows.FirstOrDefault();
                if (window?.Page is AppShell)
                    window.Page = new NavigationPage(new SignInPage());
            });

        return new Window(new SplashPage());
    }
}
