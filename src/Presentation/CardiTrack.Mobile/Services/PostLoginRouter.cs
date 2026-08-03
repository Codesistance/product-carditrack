using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Onboarding;

namespace CardiTrack.Mobile.Services;

/// <summary>
/// Decides the app root after a session exists: no server-side user record → account setup;
/// user but no CardiMember yet → the M1-04 add-member wizard (skippable); otherwise →
/// AppShell (dashboard). Call only when tokens exist; ApiException propagates to the
/// caller's error UI.
/// </summary>
public sealed class PostLoginRouter
{
    private readonly ICardiTrackApiClient _api;

    public PostLoginRouter(ICardiTrackApiClient api)
    {
        _api = api;
    }

    public async Task RouteAsync(Page current, CancellationToken ct = default)
    {
        var status = await _api.GetOnboardingStatusAsync(ct);
        Page root = !status.HasUserAccount
            ? new NavigationPage(new AccountSetupPage())
            : !status.HasCardiMember
                ? new NavigationPage(new AddCardiMemberPage())
                : new AppShell();
        await MainThread.InvokeOnMainThreadAsync(() =>
            WindowNavigation.SetRootPage(current, root));
    }
}
