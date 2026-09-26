using CardiTrack.Mobile.Core.Forms;
using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// A list of choices in the app's own popup shell, replacing <c>DisplayActionSheet</c>. The
/// platform sheet was square, system-font and system-coloured in the middle of a rounded,
/// Quicksand, gradient app — the one surface that looked like it belonged to a different
/// product. Structure, scrim and entry animation match <see cref="AppPopupPage"/> so a chooser
/// and a confirmation read as the same object.
/// </summary>
public partial class AppChooserPage : ContentPage
{
    private readonly TaskCompletionSource<string?> _result = new();
    private bool _closing;

    /// <param name="danger">
    /// The options to draw as Danger, named by the caller, in place of the word rule — for a list
    /// where a destructive word is not the destructive row. Null for the word rule.
    /// </param>
    public AppChooserPage(
        string title, string cancelText, IReadOnlyList<string> options, IReadOnlyCollection<string>? danger = null)
    {
        InitializeComponent();
        // Without OverFullScreen, iOS removes the page underneath and the transparent
        // modal renders over black.
        On<iOS>().SetModalPresentationStyle(UIModalPresentationStyle.OverFullScreen);

        TitleLabel.Text = title;
        CancelBtn.Text = cancelText;

        foreach (var option in options)
        {
            // An option that removes or ends something is drawn as a Danger button, by the same
            // word rule the action buttons follow (ActionLooks), so "Delete permanently" reads as
            // the one to think about before tapping, wherever a caller offers it. M, not L: a
            // choice in a list of four is not a page's call to action, and at 52 the stack ate the
            // card — shorter rows keep the whole list on screen.
            var isDanger = danger is not null
                ? danger.Contains(option)
                : ActionLooks.For(option).Tone == ActionTone.Red;
            var button = new AppButton
            {
                Text = option,
                Tone = isDanger ? AppButtonTone.Danger : AppButtonTone.Secondary,
                Size = AppButtonSize.M,
            };
            // The label is the identity the caller matches on when this returns, so it is what
            // the handler closes over — not the index, which shifts when a caller filters its
            // own options (snooze drops choices above a rule's ceiling).
            button.Clicked += async (_, _) => await CloseAsync(option);
            OptionsHost.Add(button);
        }

    }

    /// <summary>Completes with the chosen label, or null if cancelled or dismissed.</summary>
    public Task<string?> Result => _result.Task;

    /// <summary>Same width rule as the popup this shares its shell with; see <see cref="PopupCard"/>.</summary>
    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        PopupCard.Fit(Card, width);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = Scrim.FadeToAsync(1, 140);
        _ = Card.ScaleToAsync(1, 140, Easing.CubicOut);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // Also fires on app backgrounding and when another modal covers this one — in both
        // cases the page is still on the modal stack. Only resolve when the page left the
        // stack without CloseAsync (external dismissal, e.g. a root swap).
        if (!_closing && !Navigation.ModalStack.Contains(this))
            _result.TrySetResult(null);
    }

    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync(null);
        return true;
    }

    /// <summary>
    /// A chooser offers an out by definition — the Cancel row is right there — so tapping away
    /// is a dismissal, matching the platform sheet this replaces and AppPopupPage's own
    /// non-confirmation behaviour.
    /// </summary>
    private async void OnScrimTapped(object? sender, TappedEventArgs e) => await CloseAsync(null);

    private async void OnCancelClicked(object? sender, EventArgs e) => await CloseAsync(null);

    private async Task CloseAsync(string? choice)
    {
        if (_closing)
            return;
        _closing = true;

        try
        {
            await Task.WhenAll(
                Scrim.FadeToAsync(0, 100),
                Card.ScaleToAsync(0.92, 100, Easing.CubicIn));
            await Navigation.PopModalAsync(animated: false);
        }
        finally
        {
            _result.TrySetResult(choice);
        }
    }
}
