using System.Globalization;
using CardiTrack.Mobile.Core.Localization;
using CardiTrack.Mobile.Services;
using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// The M1-13 contact carousel's edit form: one record's own fields, in the app's popup shell.
/// </summary>
/// <remarks>
/// A form rather than a trip to M1-14. Both of these records are one or two fields, and the edit
/// screen they used to open is the whole profile — a caregiver who tapped a phone number landed
/// in a page of name, date of birth, sex, relationship, photo and medical notes, every one of
/// which is a field they can disturb on the way to the one they came for. Isolating the form to
/// the record keeps the change as small as the intent was, and keeps the caregiver on the screen
/// they were reading.
/// </remarks>
public partial class ContactEditPopupPage : ContentPage
{
    /// <summary>The server's own ceiling on an emergency contact name.</summary>
    private const int MaxNameLength = 100;

    /// <summary>How long the caret waits for the card it belongs to; see <see cref="FocusFirstFieldAsync"/>.</summary>
    private const int FocusDelayMs = 160;

    private readonly TaskCompletionSource<ContactEdit?> _result = new();
    private readonly bool _hasName;
    private bool _closing;

    public ContactEditPopupPage(ContactEditKind kind, string? name, string? phone)
    {
        InitializeComponent();
        // Without OverFullScreen, iOS removes the page underneath and the transparent
        // modal renders over black.
        On<iOS>().SetModalPresentationStyle(UIModalPresentationStyle.OverFullScreen);

        _hasName = kind is ContactEditKind.EmergencyContact;

        // Titled for the card it was opened from, so the popup reads as that card's own form
        // rather than as a screen the caregiver has been taken to.
        (TitleLabel.Text, DescriptionLabel.Text) = kind switch
        {
            ContactEditKind.EmergencyContact => (
                "Emergency Contact",
                "The person to reach first if something looks wrong."),
            _ => (
                "Phone Number",
                "Their own number — what the call and message buttons on this card use."),
        };

        NameFieldSection.IsVisible = _hasName;
        NameFieldLabel.Text = "Name";
        NameEntry.Text = name;

        PhoneFieldLabel.Text = _hasName ? "Emergency contact number" : "Phone number";
        PhoneEntry.Text = phone;
        PhoneEntry.Placeholder = PhonePlaceholder.ForRegion(RegionInfo.CurrentRegion.TwoLetterISORegionName);
    }

    /// <summary>Completes with the edited record, or null when cancelled or dismissed.</summary>
    public Task<ContactEdit?> Result => _result.Task;

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
        _ = FocusFirstFieldAsync();
    }

    /// <summary>
    /// Puts the caret in the first field the form has. This popup exists because the caregiver
    /// came to type one thing, so the keyboard should already be up and pointed at it.
    /// </summary>
    /// <remarks>
    /// Behind the entry animation rather than in <c>OnAppearing</c> itself: a freshly pushed
    /// modal has no platform handler for the entry yet on that first pass, and <c>Focus()</c> on
    /// a view without one is a no-op that reports failure and moves on. Waiting also puts the
    /// keyboard on screen after the card rather than under it on its way up.
    /// </remarks>
    private async Task FocusFirstFieldAsync()
    {
        await Task.Delay(FocusDelayMs);
        if (!_closing)
            (_hasName ? (VisualElement)NameEntry : PhoneEntry).Focus();
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
    /// Tapping away closes an empty form and is ignored by one holding anything — a card with a
    /// name and a number in it is a record the caregiver could lose to a mistimed tap, and Cancel
    /// is right there to lose it deliberately. AppPopupPage draws the same line between a message
    /// that can be dismissed and a confirmation that has to be answered.
    /// </summary>
    private async void OnScrimTapped(object? sender, TappedEventArgs e)
    {
        if (!HasTyping())
            await CloseAsync(null);
    }

    /// <summary>Return on the name moves to the number rather than submitting a half-filled form.</summary>
    private void OnNameCompleted(object? sender, EventArgs e) => PhoneEntry.Focus();

    private async void OnPhoneCompleted(object? sender, EventArgs e) => await SubmitAsync();

    private async void OnSaveClicked(object? sender, EventArgs e) => await SubmitAsync();

    private async void OnCancelClicked(object? sender, EventArgs e) => await CloseAsync(null);

    private async Task SubmitAsync()
    {
        if (_closing || !Validate())
            return;

        await CloseAsync(new ContactEdit(
            _hasName ? NullIfEmpty(NameEntry.Text) : null,
            NullIfEmpty(PhoneEntry.Text)));
    }

    /// <summary>
    /// The same two rules the API applies, so nothing typed here is sent off only to be refused —
    /// see <see cref="PhoneNumberFormat"/>. Both fields stay optional: clearing them is how an
    /// emergency contact is removed, and a form that demands a number cannot be that.
    /// </summary>
    private bool Validate()
    {
        NameError.IsVisible = false;
        PhoneError.IsVisible = false;

        var valid = true;

        if (_hasName && (NameEntry.Text?.Trim().Length ?? 0) > MaxNameLength)
        {
            NameError.Text = $"Name cannot exceed {MaxNameLength} characters";
            NameError.IsVisible = true;
            valid = false;
        }

        if (!PhoneNumberFormat.IsValid(PhoneEntry.Text))
        {
            PhoneError.Text = PhoneNumberFormat.ValidationMessage;
            PhoneError.IsVisible = true;
            valid = false;
        }

        return valid;
    }

    private bool HasTyping() =>
        !string.IsNullOrWhiteSpace(PhoneEntry.Text)
        || (_hasName && !string.IsNullOrWhiteSpace(NameEntry.Text));

    private async Task CloseAsync(ContactEdit? edit)
    {
        if (_closing)
            return;
        _closing = true;

        // Down before the card goes: on Android the modal pops out from under a raised keyboard,
        // which resizes the page underneath mid-animation.
        PhoneEntry.Unfocus();
        NameEntry.Unfocus();

        try
        {
            await Task.WhenAll(
                Scrim.FadeToAsync(0, 100),
                Card.ScaleToAsync(0.92, 100, Easing.CubicIn));
            await Navigation.PopModalAsync(animated: false);
        }
        finally
        {
            _result.TrySetResult(edit);
        }
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
