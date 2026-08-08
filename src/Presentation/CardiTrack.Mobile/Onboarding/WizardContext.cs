using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Mobile.Onboarding;

/// <summary>Where the wizard was launched from; decides what the terminal exits do.</summary>
public enum WizardOrigin
{
    /// <summary>First-run onboarding: the wizard is the window root; finishing hands over to the shell.</summary>
    OnboardingRoot,

    /// <summary>Launched modally over existing UI (dashboard, post-login resume); finishing pops the modal.</summary>
    Modal,
}

/// <summary>
/// Per-run wizard state threaded through the page constructors, so any flow can enter the
/// wizard at the step matching the data it already has and get control back when it exits.
/// </summary>
public sealed class WizardContext
{
    public WizardOrigin Origin { get; }

    /// <summary>Set at launch when the member already exists, or by M1-04 after creating one.</summary>
    public CardiMemberResponse? Member { get; set; }

    public bool MemberCreated { get; set; }
    public bool DeviceConnected { get; set; }

    /// <summary>
    /// Whether M1-07 hands on to the M1-08 baseline explainer before exiting. True for the
    /// member's first device — the 30-day learning story is news then. False when a second
    /// device is being added to a member who already has one: there the explainer is a
    /// detour past the exit the user asked for.
    /// </summary>
    public bool ShowBaselineIntro { get; set; } = true;

    /// <summary>
    /// Set by <see cref="GoToDashboardAsync"/>, and read by the launcher once the modal is
    /// gone: a caller that is itself pushed over a tab — device management — has been popped
    /// off the stack by then, so it must not carry on refreshing a page nobody is looking at.
    /// </summary>
    public bool ExitedToDashboard { get; private set; }

    private WizardContext(WizardOrigin origin, CardiMemberResponse? member)
    {
        Origin = origin;
        Member = member;
    }

    public static WizardContext ForOnboardingRoot() => new(WizardOrigin.OnboardingRoot, null);

    public static WizardContext ForModal(CardiMemberResponse? member) => new(WizardOrigin.Modal, member);

    public CardiMemberResponse RequireMember() =>
        Member ?? throw new InvalidOperationException("Wizard reached a device step without a CardiMember.");

    /// <summary>Terminal exit: onboarding replaces the root with the shell; modal returns to the launcher.</summary>
    public Task FinishAsync(Page current) => Origin == WizardOrigin.OnboardingRoot
        ? MainThread.InvokeOnMainThreadAsync(() => WindowNavigation.SetRootPage(current, new AppShell()))
        : current.Navigation.PopModalAsync();

    /// <summary>
    /// Terminal exit for the steps whose button names the dashboard. <see cref="FinishAsync"/>
    /// alone would only unwind the modal, landing back on whatever launched the wizard —
    /// device management, say — which is not what the button offered. As onboarding root the
    /// fresh shell opens on the dashboard tab already, so only the modal case has to navigate.
    /// </summary>
    public async Task GoToDashboardAsync(Page current)
    {
        ExitedToDashboard = true;
        await FinishAsync(current);

        // Shell.Current is null for the onboarding-root path until the swap settles, and the
        // shell it would resolve to is already showing the dashboard, so this is modal-only.
        if (Origin == WizardOrigin.Modal && Shell.Current is { } shell)
            await shell.GoToAsync(AppShell.DashboardRoute);
    }

    /// <summary>Back out from the bottom of the wizard stack. As onboarding root there is nowhere to go.</summary>
    public Task CancelAsync(Page current) => Origin == WizardOrigin.Modal
        ? current.Navigation.PopModalAsync()
        : Task.CompletedTask;
}
