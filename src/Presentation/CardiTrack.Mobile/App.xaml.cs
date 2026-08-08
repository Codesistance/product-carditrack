using CardiTrack.Mobile.Core.Auth;
using CardiTrack.Mobile.Onboarding;
using CardiTrack.Mobile.Services;
using Microsoft.Extensions.Logging;

namespace CardiTrack.Mobile;

public partial class App : Microsoft.Maui.Controls.Application
{
    /// <summary>Guards against concurrent 401s each trying to swap the root out from under the other.</summary>
    private bool _returningToSignIn;

    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        // A rejected refresh token mid-session sends the user back to sign-in. Splash and
        // the auth pages handle their own failures, so only an authenticated root reacts.
        ServiceHelper.GetRequiredService<ITokenRefresher>().SessionExpired += () =>
            MainThread.BeginInvokeOnMainThread(async () => await ReturnToSignInAsync());

        return new Window(new SplashPage());
    }

    private async Task ReturnToSignInAsync()
    {
        if (_returningToSignIn)
            return;

        var window = Windows.FirstOrDefault();
        if (window?.Page is not { } root || !IsAuthenticatedRoot(root))
            return;

        _returningToSignIn = true;
        try
        {
            await DismissModalsAsync(root);
        }
        finally
        {
            window.Page = new NavigationPage(new SignInPage(SignInPage.SessionExpiredNotice));
            _returningToSignIn = false;
        }
    }

    /// <summary>
    /// Anything modal — the connect-device wizard — comes down before the root is swapped.
    /// Replacing the root underneath a live modal leaves the modal on screen: the sign-out is
    /// invisible until the user dismisses it, and then reads as if that page navigated to
    /// sign-in on its own.
    /// </summary>
    private static async Task DismissModalsAsync(Page root)
    {
        // Bounded by the stack we found: a pop that doesn't shrink the stack must not spin.
        for (var remaining = root.Navigation.ModalStack.Count; remaining > 0; remaining--)
        {
            if (root.Navigation.ModalStack.Count == 0)
                return;
            try
            {
                await root.Navigation.PopModalAsync(false);
            }
            catch (Exception ex)
            {
                // Best-effort: the root swap below still has to happen, or the user is left
                // holding a wizard that can no longer talk to the API.
                ServiceHelper.GetRequiredService<ILogger<App>>()
                    .LogWarning(ex, "Dismissing modals before returning to sign-in failed");
                return;
            }
        }
    }

    /// <summary>
    /// Roots that only exist for a signed-in user. Splash, Welcome and the auth pages are
    /// excluded — they surface their own auth failures and must not be yanked mid-flow.
    /// </summary>
    private static bool IsAuthenticatedRoot(Page root) => root switch
    {
        AppShell => true,
        // PostLoginRouter can root the app at onboarding; a session that dies there was
        // previously ignored, leaving the user to finish the wizard against dead tokens.
        NavigationPage nav => nav.RootPage is AccountSetupPage or AddCardiMemberPage,
        _ => false,
    };
}
