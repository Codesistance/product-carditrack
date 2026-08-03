using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Onboarding;

namespace CardiTrack.Mobile.Services;

/// <summary>
/// Decides the app root after a session exists: server-side user record present → AppShell
/// (dashboard); otherwise → account setup to provision the organization and user.
/// Call only when tokens exist; ApiException propagates to the caller's error UI.
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
        Page root = status.HasUserAccount
            ? new AppShell()
            : new NavigationPage(new AccountSetupPage());
        await MainThread.InvokeOnMainThreadAsync(() =>
            WindowNavigation.SetRootPage(current, root));
    }
}
