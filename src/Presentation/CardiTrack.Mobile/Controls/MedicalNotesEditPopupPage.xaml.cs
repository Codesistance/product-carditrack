using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// The health background's own edit form, in the app's popup shell.
/// </summary>
/// <remarks>
/// <para>
/// A form rather than a trip to M1-14, on the same reasoning as
/// <see cref="ContactEditPopupPage"/>. The notes are one field, and the screen they used to open
/// is the whole profile: a caregiver who tapped "Add notes" landed in a page of name, date of
/// birth, sex, relationship and photo, every one of which is a field they can disturb on the way
/// to the one they came for.
/// </para>
/// <para>
/// It also makes the asking repeatable, which is the point of dating the background at all. A
/// notification saying it has been six months can put this in front of somebody in one tap, and
/// they can answer it without leaving the screen they were on.
/// </para>
/// </remarks>
public partial class MedicalNotesEditPopupPage : ContentPage
{
    /// <summary>The server's own ceiling on the notes; also the Editor's <c>MaxLength</c>.</summary>
    public const int MaxLength = 2000;

    /// <summary>How close to the ceiling the remaining count starts being worth showing.</summary>
    private const int CounterAppearsWithin = 200;

    /// <summary>How long the caret waits for the card it belongs to; see <see cref="FocusAsync"/>.</summary>
    private const int FocusDelayMs = 160;

    private readonly TaskCompletionSource<string?> _result = new();
    private readonly string? _original;
    private bool _closing;

    public MedicalNotesEditPopupPage(string? firstName, string? notes)
    {
        InitializeComponent();
        // Without OverFullScreen, iOS removes the page underneath and the transparent
        // modal renders over black.
        On<iOS>().SetModalPresentationStyle(UIModalPresentationStyle.OverFullScreen);

        _original = notes;

        var who = string.IsNullOrWhiteSpace(firstName) ? "them" : firstName;

        // The description changes with the path, because the two are not the same request:
        // starting a background from nothing is a different thing from checking one over.
        TitleLabel.Text = "Health Background";
        DescriptionLabel.Text = string.IsNullOrWhiteSpace(notes)
            ? $"Anything a caregiver arriving in a hurry should have read about {who} already."
            : $"Keep this current — it is read alongside every one of {who}'s readings.";

        NotesEditor.Text = notes;
        UpdateCounter();
    }

    /// <summary>
    /// Completes with the edited notes, null when cancelled or dismissed, and an empty string when
    /// the caregiver deliberately cleared them — which is how a background is removed, and is not
    /// the same answer as walking away.
    /// </summary>
    public Task<string?> Result => _result.Task;

    /// <summary>Same width rule as the popups this shares its shell with; see <see cref="PopupCard"/>.</summary>
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
        _ = FocusAsync();
    }

    /// <summary>
    /// Puts the caret in the notes. This popup exists because the caregiver came to type, so the
    /// keyboard should already be up and pointed at the field.
    /// </summary>
    /// <remarks>
    /// Behind the entry animation rather than in <c>OnAppearing</c> itself, for the reason
    /// <see cref="ContactEditPopupPage"/> records: a freshly pushed modal has no platform handler
    /// for the field yet on that first pass, and focusing a view without one is a no-op that
    /// reports failure and moves on.
    /// </remarks>
    private async Task FocusAsync()
    {
        await Task.Delay(FocusDelayMs);
        if (!_closing)
            NotesEditor.Focus();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // Also fires on app backgrounding and when another modal covers this one — in both cases
        // the page is still on the modal stack. Only resolve when the page left the stack without
        // CloseAsync (external dismissal, e.g. a root swap).
        if (!_closing && !Navigation.ModalStack.Contains(this))
            _result.TrySetResult(null);
    }

    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync(null);
        return true;
    }

    /// <summary>
    /// Tapping away closes a form nobody has changed and is ignored by one holding edits. The same
    /// line <see cref="ContactEditPopupPage"/> draws, and it matters more here: this field holds
    /// paragraphs, and losing them to a mistimed tap at the edge of the card is not a small loss.
    /// Cancel is right there for losing them on purpose.
    /// </summary>
    private async void OnScrimTapped(object? sender, TappedEventArgs e)
    {
        if (!HasEdits())
            await CloseAsync(null);
    }

    private void OnNotesChanged(object? sender, TextChangedEventArgs e) => UpdateCounter();

    /// <summary>
    /// Shows what is left only near the ceiling. A counter sitting under an empty box on every
    /// open is chrome about a limit almost nobody reaches; one that appears as somebody approaches
    /// it answers a question they are about to have.
    /// </summary>
    private void UpdateCounter()
    {
        var remaining = MaxLength - (NotesEditor.Text?.Length ?? 0);
        CounterLabel.IsVisible = remaining <= CounterAppearsWithin;
        CounterLabel.Text = remaining == 1 ? "1 character left" : $"{remaining} characters left";
    }

    private async void OnSaveClicked(object? sender, EventArgs e) => await SubmitAsync();

    private async void OnCancelClicked(object? sender, EventArgs e) => await CloseAsync(null);

    private async Task SubmitAsync()
    {
        if (_closing || !Validate())
            return;

        // Empty string rather than null when the field was cleared: null is this popup's word for
        // "nothing happened", and a caregiver who deleted the notes did something.
        await CloseAsync(NotesEditor.Text?.Trim() ?? string.Empty);
    }

    /// <summary>
    /// The one rule the server applies. <c>MaxLength</c> on the Editor should make this
    /// unreachable — the keyboard stops at the ceiling — but a platform that honours it loosely,
    /// or a paste, must not produce a save the API refuses over text the caregiver cannot see is
    /// too long.
    /// </summary>
    private bool Validate()
    {
        NotesError.IsVisible = false;

        if ((NotesEditor.Text?.Trim().Length ?? 0) <= MaxLength)
            return true;

        NotesError.Text = $"Notes cannot exceed {MaxLength} characters";
        NotesError.IsVisible = true;
        return false;
    }

    private bool HasEdits() =>
        !string.Equals(
            NotesEditor.Text?.Trim() ?? string.Empty,
            _original?.Trim() ?? string.Empty,
            StringComparison.Ordinal);

    private async Task CloseAsync(string? notes)
    {
        if (_closing)
            return;
        _closing = true;

        // Down before the card goes: on Android the modal pops out from under a raised keyboard,
        // which resizes the page underneath mid-animation.
        NotesEditor.Unfocus();

        try
        {
            await Task.WhenAll(
                Scrim.FadeToAsync(0, 100),
                Card.ScaleToAsync(0.92, 100, Easing.CubicIn));
            await Navigation.PopModalAsync(animated: false);
        }
        finally
        {
            _result.TrySetResult(notes);
        }
    }
}
