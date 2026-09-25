using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Devices;

namespace CardiTrack.Mobile.Onboarding;

/// <summary>Outcome of a modal wizard run, reported once the modal is dismissed by any path.</summary>
/// <param name="MemberCreated">A CardiMember was created during the run.</param>
/// <param name="DeviceConnected">A device was connected during the run.</param>
/// <param name="ExitedToDashboard">
/// The run ended on "Go to Dashboard", which navigates the shell there rather than returning
/// to the launcher. A caller pushed above a tab is no longer on screen once this is set.
/// </param>
public readonly record struct WizardResult(
    bool MemberCreated, bool DeviceConnected, bool ExitedToDashboard);

/// <summary>
/// Launches the add-member / connect-device wizard modally over whatever UI needs it.
/// The entry step follows the data the caller already has: no member → M1-04, member → M1-05.
/// </summary>
internal static class WizardLauncher
{
    /// <summary>Set when the post-login device-setup resume is dismissed, so it doesn't nag every launch.</summary>
    public const string ResumeDismissedKey = "DeviceSetupResumeDismissed";

    /// <summary>
    /// Pushes the wizard in its own modal <see cref="NavigationPage"/> stack. The returned task
    /// completes when the wizard exits — Cancel / Done / hardware back / iOS swipe via
    /// <c>ModalPopped</c>, or "Go to Dashboard" via <see cref="WizardContext.DashboardExit"/>
    /// after the root swap (the pop that path does first is ignored so callers do not
    /// resume before the new shell is up).
    /// </summary>
    /// <param name="showBaselineIntro">
    /// Pass false when the member already has a connected device, so success exits straight
    /// back to the caller instead of via the M1-08 baseline explainer.
    /// </param>
    /// <param name="reconnectDevice">
    /// Set to send an existing connection straight into M1-06 for its own brand, skipping
    /// M1-05's picker — the caregiver already told us which device is broken by tapping
    /// Reconnect on it. Ignored when <paramref name="member"/> is null: M1-04 has to run first.
    /// </param>
    /// <param name="reconnectDeviceId">
    /// The connection being reconnected, so the grant is stored on it (and consent is asked for
    /// again) rather than matched to it by account after the fact.
    /// </param>
    public static async Task<WizardResult> RunModalAsync(
        INavigation navigation, CardiMemberResponse? member, bool showBaselineIntro = true,
        ConnectableDevice? reconnectDevice = null, Guid? reconnectDeviceId = null)
    {
        var ctx = WizardContext.ForModal(member);
        ctx.ShowBaselineIntro = showBaselineIntro;
        Page entry = member is null
            ? new AddCardiMemberPage(ctx)
            : reconnectDevice is not null
                ? new DeviceConnectionPage(ctx, reconnectDevice, reconnectDeviceId)
                : new DeviceSelectionPage(ctx);
        var wizardNav = new NavigationPage(entry);
        var tcs = new TaskCompletionSource<WizardResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var app = global::Microsoft.Maui.Controls.Application.Current!;
        void Complete()
        {
            app.ModalPopped -= OnPopped;
            ctx.DashboardExit -= OnDashboardExit;
            tcs.TrySetResult(new WizardResult(ctx.MemberCreated, ctx.DeviceConnected, ctx.ExitedToDashboard));
        }

        void OnPopped(object? sender, ModalPoppedEventArgs e)
        {
            if (!ReferenceEquals(e.Modal, wizardNav))
                return;
            // "Go to Dashboard" pops first, then swaps the root. Completing here
            // would release the caller (and let Dashboard LoadAsync) before the
            // new shell is up. DashboardExit finishes that path after the swap.
            if (ctx.ExitedToDashboard)
                return;
            Complete();
        }

        void OnDashboardExit(object? sender, EventArgs e) => Complete();

        app.ModalPopped += OnPopped;
        ctx.DashboardExit += OnDashboardExit;
        try
        {
            await navigation.PushModalAsync(wizardNav);
        }
        catch
        {
            // The modal never went up, so ModalPopped will never fire for it. Without
            // this the handler — and the context, nav stack and TCS it captures — stays
            // on an application-lifetime event, one leak per failed attempt, and the
            // awaited task below could never complete.
            app.ModalPopped -= OnPopped;
            ctx.DashboardExit -= OnDashboardExit;
            throw;
        }
        return await tcs.Task;
    }
}
