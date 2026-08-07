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
    public static async Task<WizardResult> RunModalAsync(INavigation navigation, CardiMemberResponse? member)
    {
        var ctx = WizardContext.ForModal(member);
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
        await navigation.PushModalAsync(wizardNav);
        return await tcs.Task;
    }
}
