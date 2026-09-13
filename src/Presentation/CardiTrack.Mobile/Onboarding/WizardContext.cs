using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Services;

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

    /// <summary>
    /// Fired when "Go to Dashboard" replaces the window root. The modal is never popped on
    /// that path, so <c>ModalPopped</c> does not run and the launcher has to hear this
    /// instead or it waits forever for a result that will not come.
    /// </summary>
    public event EventHandler? DashboardExit;

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
    /// Terminal exit for the step whose button names the dashboard. Always roots a fresh
    /// <see cref="AppShell"/> on the dashboard tab — never pops the wizard, never walks
    /// back into the OAuth browser that authorized the device, and never returns to
    /// whatever launched the wizard (device management, a post-login resume).
    /// </summary>
    public async Task GoToDashboardAsync(Page current)
    {
        ExitedToDashboard = true;

        // A modal pop (or any ".." unwind) leaves the Custom Tab / Chrome task as the
        // next thing Android shows. Replace the window so there is no history to walk
        // back through, then bring this task to the front after the shell is up.
        var shell = new AppShell();
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            WindowNavigation.SetRootPage(current, shell);
        });
        await GoToDashboardTabAsync(shell);
        AppForeground.BringToFront();
        DashboardExit?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Absolute tab route, not ".." and not a coincidence of which page is underneath the
    /// modal. Waits for the shell to load when this is a freshly rooted AppShell, so we do
    /// not race handler creation the way a same-breath GoToAsync would on Android.
    /// </summary>
    private static async Task GoToDashboardTabAsync(Shell shell)
    {
        if (!shell.IsLoaded)
        {
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnLoaded(object? sender, EventArgs e)
            {
                shell.Loaded -= OnLoaded;
                loaded.TrySetResult();
            }
            shell.Loaded += OnLoaded;
            if (shell.IsLoaded)
            {
                shell.Loaded -= OnLoaded;
                loaded.TrySetResult();
            }
            else
            {
                // No window (tests, a failed root swap) would leave this waiting forever.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    await loaded.Task.WaitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    shell.Loaded -= OnLoaded;
                    return;
                }
            }
        }

        await shell.GoToAsync(AppShell.DashboardRoute);
    }

    /// <summary>Back out from the bottom of the wizard stack. As onboarding root there is nowhere to go.</summary>
    public Task CancelAsync(Page current) => Origin == WizardOrigin.Modal
        ? current.Navigation.PopModalAsync()
        : Task.CompletedTask;
}
