using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Auth;
using CardiTrack.Mobile.Core.Offline;
using CardiTrack.Mobile.Core.Onboarding;
using CardiTrack.Mobile.Onboarding;
using Microsoft.Extensions.Logging;

namespace CardiTrack.Mobile.Services;

/// <summary>
/// Decides the app root after a session exists: no server-side user record → account setup;
/// user but no CardiMember yet → the M1-04 add-member wizard (skippable); otherwise →
/// AppShell (dashboard). When a CardiMember exists but no device is connected, the device
/// leg of the wizard is resumed modally over the dashboard — once; dismissing it sets a
/// preference so the prompt doesn't nag on every launch (the dashboard's "Connect a device"
/// card remains the standing entry point). Call only when tokens exist; ApiException
/// propagates to the caller's error UI.
/// </summary>
public sealed class PostLoginRouter
{
    private readonly ICardiTrackApiClient _api;
    private readonly IOfflineCacheWarmer _cacheWarmer;
    private readonly IPopupService _popups;
    private readonly IAuthService _auth;
    private readonly ILogger<PostLoginRouter>? _logger;

    public PostLoginRouter(
        ICardiTrackApiClient api,
        IOfflineCacheWarmer cacheWarmer,
        IPopupService popups,
        IAuthService auth,
        ILogger<PostLoginRouter>? logger = null)
    {
        _api = api;
        _cacheWarmer = cacheWarmer;
        _popups = popups;
        _auth = auth;
        _logger = logger;
    }

    public async Task RouteAsync(Page current, CancellationToken ct = default)
    {
        // Asked before onboarding status, because an account awaiting deletion is refused
        // everything else: the gate 403s the onboarding call, and a caregiver signing in to take
        // their deletion back would have met a generic "couldn't load your account" instead of the
        // one screen that can help them. This is the only route out of a pending deletion, so it
        // runs first.
        if (await OfferedToCancelDeletionAsync(current, ct))
            return;

        // Past the deletion gate, so an account that is only here to cancel its deletion is not
        // counted as a session. Every sign-in comes through here, which makes this the one place
        // session telemetry learns someone is signed in; nothing is sent before it.
        DiagnosticsConsent.SignedIn(_auth.CurrentUserEmail);

        OnboardingStatusResponse status;
        try
        {
            status = await _api.GetOnboardingStatusAsync(ct);
        }
        catch (ApiException ex) when (ex.IsNetworkFailure)
        {
            // Last-known-good GET cache is the usual path; this is the upgrade/first-offline
            // case where onboarding status was never snapshotted but a previous session
            // already chose a CardiMember. Don't resume the device wizard — it needs the
            // network, and the dashboard's connect card is still there.
            if (!Guid.TryParse(Preferences.Default.Get("PrimaryCardiMemberId", string.Empty), out _))
                throw;

            _logger?.LogInformation(
                "Onboarding status unreachable; opening the dashboard from the remembered CardiMember");
            status = new OnboardingStatusResponse
            {
                HasOrganization = true,
                HasUserAccount = true,
                HasCardiMember = true,
                HasDeviceConnected = true,
                IsOnboardingComplete = true,
            };
        }

        var route = PostLoginRouteResolver.Resolve(
            status, Preferences.Default.Get(WizardLauncher.ResumeDismissedKey, false));

        Page root = route.Destination switch
        {
            PostLoginDestination.AccountSetup => new NavigationPage(new AccountSetupPage()),
            PostLoginDestination.AddCardiMember =>
                new NavigationPage(new AddCardiMemberPage(WizardContext.ForOnboardingRoot())),
            _ => new AppShell(),
        };

        // Before the root swap, not after: the model load this may start takes about a minute
        // (docs/technical/medgemma_serving_architecture.md §9.1a), and the caregiver is about to
        // spend some of that minute reading the dashboard. Every millisecond earlier is one
        // fewer they wait on their first question. Only for the dashboard — a caregiver still in
        // the wizard has no member to ask about yet.
        if (root is AppShell)
        {
            _ = WarmAssistantAsync(ct);
            // A push may have started this already; the warmer single-flights. Starting
            // here covers the signed-in open that never saw the push (iOS killed the
            // process, the caregiver opened the icon rather than the banner).
            _ = WarmCacheAsync(ct);
        }

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            // Raised before the root swap, because the swap is what makes the dashboard appear:
            // its OnAppearing can run before the shell's Loaded below, and has to see that a
            // wizard is about to go up over it.
            var resuming = route.ResumeDeviceSetup && root is AppShell;
            DeviceSetupResumePending = resuming;
            WindowNavigation.SetRootPage(current, root);
            if (!resuming || root is not AppShell shell)
                return;

            // Push the wizard only once the shell is on screen — pushing a modal in the
            // same breath as the root swap races handler creation on Android.
            void OnLoaded(object? sender, EventArgs e)
            {
                shell.Loaded -= OnLoaded;
                _ = ResumeDeviceSetupAsync(shell);
            }
            shell.Loaded += OnLoaded;
        });
    }

    /// <summary>
    /// Asks the API to get the assistant ready, and forgets about it. Nothing here is worth
    /// failing a launch over: the endpoint answers 202 without doing the work inline, and if the
    /// call never lands the first chat question simply pays the model load as it always did.
    /// </summary>
    private async Task WarmCacheAsync(CancellationToken ct)
    {
        try
        {
            await _cacheWarmer.RefreshAsync(ct);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Warming the on-device cache after login failed.");
        }
    }

    /// <summary>
    /// If this account is awaiting deletion, asks whether to call it off. Returns true when the
    /// caller should stop — either the account is still going, or cancelling failed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Declining is a real answer, not a dead end: the caregiver is signed out again, because
    /// every other endpoint refuses them and there is no app to show. Cancelling falls through to
    /// ordinary routing, so the next thing they see is their dashboard.
    /// </para>
    /// <para>
    /// A failure to read the status is deliberately not fatal here. The endpoint is reachable for
    /// accounts that are <em>not</em> being deleted too, so an error means "unknown", and treating
    /// unknown as "being deleted" would strand a caregiver whose network hiccuped at the one
    /// moment it mattered. Onboarding status is asked next and will fail loudly if the account
    /// really is gated.
    /// </para>
    /// </remarks>
    private async Task<bool> OfferedToCancelDeletionAsync(Page current, CancellationToken ct)
    {
        AccountDeletionStatusResponse deletion;
        try
        {
            deletion = await _api.GetAccountDeletionAsync(ct);
        }
        catch (ApiException ex)
        {
            _logger?.LogInformation(
                ex, "Could not read deletion status after sign-in; carrying on to onboarding.");
            return false;
        }

        if (!deletion.DeletionRequested)
            return false;

        var due = deletion.ScheduledForUtc?.ToLocalTime();
        var keep = await _popups.ConfirmWarningAsync(
            due is { } when_
                ? $"This account is set to be deleted on {when_:d MMMM yyyy}, and monitoring has "
                  + "stopped until then. Do you want to keep it?"
                : "This account is set to be deleted and monitoring has stopped until then. "
                  + "Do you want to keep it?",
            "Your account is being deleted",
            confirmText: "Keep my account",
            cancelText: "Go on deleting it");

        if (!keep)
        {
            // Nothing else will load for them, so leaving them inside the app would be a worse
            // answer than the sign-in page they came from.
            // Stop session telemetry first: this can run again during a live session (a retry),
            // after an earlier pass already signed telemetry in.
            await MainThread.InvokeOnMainThreadAsync(DiagnosticsConsent.SignedOut);
            await _auth.SignOutAsync();
            await MainThread.InvokeOnMainThreadAsync(() =>
                WindowNavigation.SetRootPage(current, new NavigationPage(new SignInPage())));
            return true;
        }

        try
        {
            await _api.CancelAccountDeletionAsync(ct);
            return false;
        }
        catch (ApiException ex)
        {
            await _popups.ShowWarningAsync(ex.Message, "Couldn't stop the deletion");
            // Stop session telemetry first: this can run again during a live session (a retry),
            // after an earlier pass already signed telemetry in.
            await MainThread.InvokeOnMainThreadAsync(DiagnosticsConsent.SignedOut);
            await _auth.SignOutAsync();
            await MainThread.InvokeOnMainThreadAsync(() =>
                WindowNavigation.SetRootPage(current, new NavigationPage(new SignInPage())));
            return true;
        }
    }

    private async Task WarmAssistantAsync(CancellationToken ct)
    {
        try
        {
            await _api.PrepareAssistantAsync(ct);
        }
        catch (Exception ex)
        {
            // Debug, not Warning: offline launches are ordinary, and this failing is invisible to
            // the caregiver by design. Started fire-and-forget, so the catch is also what keeps
            // it from surfacing as an unobserved task exception.
            _logger?.LogDebug(ex, "Preparing the assistant after login failed.");
        }
    }

    /// <summary>
    /// True from the moment a sign-in decides to reopen the device-setup wizard until that attempt
    /// has finished. The wizard is pushed from the shell's Loaded event, which can arrive after the
    /// dashboard has already appeared, so a screen that opens its own modal on arrival — the
    /// telemetry notice — checks this as well as the modal stack. Main thread only.
    /// </summary>
    internal static bool DeviceSetupResumePending { get; private set; }

    private async Task ResumeDeviceSetupAsync(AppShell shell)
    {
        try
        {
            var member = PrimaryCardiMember.From(await _api.GetCardiMembersAsync());
            if (member is null)
                return;

            var result = await MainThread.InvokeOnMainThreadAsync(() =>
                WizardLauncher.RunModalAsync(shell.Navigation, member));
            if (!result.DeviceConnected)
                Preferences.Default.Set(WizardLauncher.ResumeDismissedKey, true);
        }
        catch (ApiException)
        {
            // The dashboard still works; its "Connect a device" card offers the same flow.
        }
        catch (Exception ex)
        {
            // Started fire-and-forget from the Loaded handler, so anything escaping here
            // becomes an unobserved task exception rather than something a caller can
            // handle — including a failed modal push, which RunModalAsync rethrows.
            // Resuming device setup is best-effort: record it and leave the dashboard's
            // "Connect a device" card as the way in.
            _logger?.LogWarning(ex, "Resuming device setup after login failed.");
        }
        finally
        {
            DeviceSetupResumePending = false;
        }
    }
}
