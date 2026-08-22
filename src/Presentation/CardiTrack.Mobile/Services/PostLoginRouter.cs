using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Api;
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
    private readonly ILogger<PostLoginRouter>? _logger;

    public PostLoginRouter(ICardiTrackApiClient api, ILogger<PostLoginRouter>? logger = null)
    {
        _api = api;
        _logger = logger;
    }

    public async Task RouteAsync(Page current, CancellationToken ct = default)
    {
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
            _ = WarmAssistantAsync(ct);

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            WindowNavigation.SetRootPage(current, root);
            if (!route.ResumeDeviceSetup || root is not AppShell shell)
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
    }
}
