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

    /// <summary>Back out from the bottom of the wizard stack. As onboarding root there is nowhere to go.</summary>
    public Task CancelAsync(Page current) => Origin == WizardOrigin.Modal
        ? current.Navigation.PopModalAsync()
        : Task.CompletedTask;
}
