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
    /// Fired when "Go to Dashboard" finishes (success or a failed root swap). The launcher
    /// also listens to <c>ModalPopped</c>; both complete the same TCS so a pop-then-swap
    /// is still one result.
    /// </summary>
    public event EventHandler? DashboardExit;

    private int _dashboardExitBusy;

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
    /// Terminal exit for the step whose button names the dashboard. Pops any live modal
    /// (a root swap underneath one leaves the wizard on screen), then goes to
    /// <c>//dashboard</c> on the existing <see cref="AppShell"/> — or roots a new one
    /// when this is first-run onboarding and there is no shell yet.
    /// </summary>
    public async Task GoToDashboardAsync(Page current)
    {
        if (Interlocked.CompareExchange(ref _dashboardExitBusy, 1, 0) != 0)
            return;

        // Set before the pop so ModalPopped does not release callers early.
        // Cleared again if the modal will not come down — that is not a hand-off.
        ExitedToDashboard = true;

        try
        {
            // Foreground the app *before* touching the UI. The OAuth round-trip can leave a
            // Custom Tab above us in the task or a browser task ahead of ours, and popping the
            // modal and swapping the shell underneath that is what puts the caregiver back on
            // the provider's page instead of the dashboard this button named. Doing it first
            // clears the browser off the task; the repeat in the finally below wins the race
            // if the browser takes the foreground again while the navigation animates.
            AppForeground.BringToFront();

            // Capture the window first: after the modal pops, `current` is no longer
            // parented and its Window is gone.
            var window = current.Window
                ?? Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault();
            var root = window?.Page;

            // Replacing the root underneath a live modal leaves the wizard on screen
            // (App.DismissModalsAsync). Pop it first, without animation. Only swap
            // when the stack is actually empty — a failed pop must not hide a new
            // shell behind the wizard.
            var dismissed = root is null || await DismissModalsAsync(root);
            if (!dismissed)
            {
                try
                {
                    await current.Navigation.PopModalAsync(false);
                }
                catch
                {
                    // Still up. Do not swap under it.
                }
                dismissed = (window?.Page ?? root)?.Navigation.ModalStack.Count == 0;
            }

            if (!dismissed)
            {
                ExitedToDashboard = false;
                return;
            }

            // A second AppShell() re-registers every pushed-page route on the
            // process-wide Routing table and throws. Reuse the shell the modal
            // sat on. First-run onboarding has no shell yet — that is the only
            // path that constructs one.
            if (Shell.Current is { } existing)
            {
                await GoToDashboardTabAsync(existing);
                return;
            }

            var shell = new AppShell();
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (window is not null)
                    window.Page = shell;
                else
                    WindowNavigation.SetRootPage(current, shell);
            });
            await GoToDashboardTabAsync(shell);
        }
        finally
        {
            // Every exit from here — success, a modal that would not come down, or a throw —
            // owes the caregiver the app in front of them rather than the OAuth browser.
            AppForeground.BringToFront();
            DashboardExit?.Invoke(this, EventArgs.Empty);
            if (!ExitedToDashboard)
                Interlocked.Exchange(ref _dashboardExitBusy, 0);
        }
    }

    /// <summary>
    /// Bounded pop: a pop that does not shrink the stack must not spin.
    /// Returns whether the modal stack is empty afterwards.
    /// </summary>
    private static async Task<bool> DismissModalsAsync(Page root)
    {
        for (var remaining = root.Navigation.ModalStack.Count; remaining > 0; remaining--)
        {
            if (root.Navigation.ModalStack.Count == 0)
                return true;
            try
            {
                await root.Navigation.PopModalAsync(false);
            }
            catch
            {
                break;
            }
        }

        return root.Navigation.ModalStack.Count == 0;
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

        // A prior tab jump (bell, etc.) may have recorded an origin. This button
        // promised the dashboard, so back from here must not revive that page.
        TabNavigation.Origin.Clear();
        await shell.GoToAsync(AppShell.DashboardRoute);
    }

    /// <summary>Back out from the bottom of the wizard stack. As onboarding root there is nowhere to go.</summary>
    public Task CancelAsync(Page current) => Origin == WizardOrigin.Modal
        ? current.Navigation.PopModalAsync()
        : Task.CompletedTask;
}
