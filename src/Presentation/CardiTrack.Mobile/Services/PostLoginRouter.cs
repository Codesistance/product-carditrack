using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Onboarding;

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
                ? new NavigationPage(new AddCardiMemberPage(WizardContext.ForOnboardingRoot()))
                : new AppShell();

        var resumeDeviceLeg = root is AppShell
            && !status.HasDeviceConnected
            && !Preferences.Default.Get(WizardLauncher.ResumeDismissedKey, false);

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            WindowNavigation.SetRootPage(current, root);
            if (!resumeDeviceLeg)
                return;

            // Push the wizard only once the shell is on screen — pushing a modal in the
            // same breath as the root swap races handler creation on Android.
            var shell = (AppShell)root;
            void OnLoaded(object? sender, EventArgs e)
            {
                shell.Loaded -= OnLoaded;
                _ = ResumeDeviceSetupAsync(shell);
            }
            shell.Loaded += OnLoaded;
        });
    }

    private async Task ResumeDeviceSetupAsync(AppShell shell)
    {
        try
        {
            var members = await _api.GetCardiMembersAsync();
            var member = members.FirstOrDefault(m => m.IsActive) ?? members.FirstOrDefault();
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
    }
}
