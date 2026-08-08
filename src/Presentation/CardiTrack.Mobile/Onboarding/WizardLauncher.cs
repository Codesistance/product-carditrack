using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Mobile.Onboarding;

/// <summary>Outcome of a modal wizard run, reported once the modal is dismissed by any path.</summary>
public readonly record struct WizardResult(bool MemberCreated, bool DeviceConnected);

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
    /// completes when the modal is dismissed — wizard exit, Android hardware back, or iOS swipe —
    /// via the application's ModalPopped event, so callers can always await the outcome.
    /// </summary>
    /// <param name="showBaselineIntro">
    /// Pass false when the member already has a connected device, so success exits straight
    /// back to the caller instead of via the M1-08 baseline explainer.
    /// </param>
    public static async Task<WizardResult> RunModalAsync(
        INavigation navigation, CardiMemberResponse? member, bool showBaselineIntro = true)
    {
        var ctx = WizardContext.ForModal(member);
        ctx.ShowBaselineIntro = showBaselineIntro;
        Page entry = member is null ? new AddCardiMemberPage(ctx) : new DeviceSelectionPage(ctx);
        var wizardNav = new NavigationPage(entry);
        var tcs = new TaskCompletionSource<WizardResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var app = global::Microsoft.Maui.Controls.Application.Current!;
        void OnPopped(object? sender, ModalPoppedEventArgs e)
        {
            if (!ReferenceEquals(e.Modal, wizardNav))
                return;
            app.ModalPopped -= OnPopped;
            tcs.TrySetResult(new WizardResult(ctx.MemberCreated, ctx.DeviceConnected));
        }

        app.ModalPopped += OnPopped;
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
            throw;
        }
        return await tcs.Task;
    }
}
