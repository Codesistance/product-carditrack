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

        // The app has one theme. Every page paints an explicit light ground, but the MAUI
        // template's base styles still carry AppThemeBindings that flip a Page's background to
        // OffBlack and some text to white under a dark OS theme — half a dark mode, in which
        // Pickers and search fields on white cards went white-on-white. Pinning the theme means
        // those bindings never flip, and text everywhere can use the one ink the type roles
        // define. Dark mode, if it ever comes, is a design-system job (see the Type Audit).
        UserAppTheme = AppTheme.Light;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        // A rejected refresh token mid-session sends the user back to sign-in. Splash and
        // the auth pages handle their own failures, so only an authenticated root reacts.
        ServiceHelper.GetRequiredService<ITokenRefresher>().SessionExpired += () =>
            MainThread.BeginInvokeOnMainThread(async () => await ReturnToSignInAsync());

        var window = new Window(new SplashPage());

        // Coming back to the app is the same moment as arriving at a screen: the page in front
        // of the caregiver reloads itself so the data the workers processed while the app was
        // away is already there, without a pull-to-refresh. Window-level because MAUI raises
        // the foreground transition here and nowhere else; the notifier fans it out to pages.
        var resumes = ServiceHelper.GetRequiredService<AppResumeNotifier>();
        window.Resumed += (_, _) => resumes.NotifyResumed();

        return window;
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
            // DismissModalsAsync swallows its own failures, so the root still swaps when the
            // wizard refuses to come down — better a sign-in page behind a stuck modal than
            // no sign-in page at all.
            await DismissModalsAsync(root);
            window.Page = new NavigationPage(new SignInPage(SignInPage.SessionExpiredNotice));
        }
        catch (Exception ex)
        {
            // Reached from a fire-and-forget handler on the UI thread: anything escaping here
            // kills the app while it is already coping with a dead session.
            LogWarning(ex, "Returning to sign-in after session expiry failed");
        }
        finally
        {
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
                // Best-effort: the root swap still has to happen, or the user is left
                // holding a wizard that can no longer talk to the API.
                LogWarning(ex, "Dismissing modals before returning to sign-in failed");
                return;
            }
        }
    }

    /// <summary>
    /// Logging is the last thing standing between a failed sign-out and a crash, so it must
    /// not be the thing that throws — resolving the logger needs a live service provider.
    /// </summary>
    private static void LogWarning(Exception ex, string message)
    {
        try
        {
            ServiceHelper.GetRequiredService<ILogger<App>>().LogWarning(ex, message);
        }
        catch
        {
            // Nothing left to report it with.
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
