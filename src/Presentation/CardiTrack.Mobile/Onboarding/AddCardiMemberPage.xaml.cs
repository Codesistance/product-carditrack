using System.Globalization;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Localization;
using CardiTrack.Mobile.Core.Media;
using CardiTrack.Mobile.Core.Onboarding;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile.Onboarding;

/// <summary>M1-04: add the first CardiMember, or skip straight to the dashboard.</summary>
public partial class AddCardiMemberPage : ContentPage
{
    // Order shown in the Figma picker; Display names match RelationshipType.
    private static readonly (string Label, RelationshipType Value)[] Relationships =
    [
        ("Parent", RelationshipType.Parent),
        ("Grandparent", RelationshipType.Grandparent),
        ("Spouse", RelationshipType.Spouse),
        ("Sibling", RelationshipType.Sibling),
        ("Other", RelationshipType.Other),
    ];

    /// <summary>
    /// Only the two values a clinical reading can use. <see cref="Gender.PreferNotToSay"/> exists
    /// in the enum but is deliberately absent here — it is the state of a member nobody was asked
    /// about, not an answer to offer, and the whole reason for this field is that every member
    /// created before it silently held that value.
    /// </summary>
    private static readonly (string Label, Gender Value)[] Sexes =
    [
        ("Male", Gender.Male),
        ("Female", Gender.Female),
    ];

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;
    private readonly CardiMemberDraftStore _drafts;
    private readonly IProfilePhotoTranscoder _photoTranscoder;
    private readonly WizardContext _ctx;
    private string? _photoPath;
    private bool _dobTouched;
    private bool _draftRestored;
    private bool _submitted;
    private Window? _window;

    public AddCardiMemberPage(WizardContext ctx)
    {
        InitializeComponent();
        _api = ServiceHelper.GetRequiredService<ICardiTrackApiClient>();
        _popups = ServiceHelper.GetRequiredService<IPopupService>();
        _drafts = ServiceHelper.GetRequiredService<CardiMemberDraftStore>();
        _photoTranscoder = ServiceHelper.GetRequiredService<IProfilePhotoTranscoder>();
        _ctx = ctx;

        if (ctx.Origin == WizardOrigin.Modal)
        {
            // Mid-flow entry: the onboarding "Step N of 4" story doesn't apply.
            Header.Step = string.Empty;
            Header.Progress = 0;
        }

        RelationshipPicker.ItemsSource = Relationships.Select(r => r.Label).ToList();
        SexPicker.ItemsSource = Sexes.Select(s => s.Label).ToList();
        DobPicker.MaximumDate = DateTime.Today;
        DobPicker.MinimumDate = DateTime.Today.AddYears(-120);
        EmergencyPhoneEntry.Placeholder = PhonePlaceholder.ForRegion(RegionInfo.CurrentRegion.TwoLetterISORegionName);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // Stopped fires as the OS backgrounds us — the moment before it may reclaim the
        // process, and the only reliable signal that the typing is about to be at risk.
        _window = Window ?? global::Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault();
        if (_window is not null)
            _window.Stopped += OnWindowStopped;

        if (_draftRestored)
            return;
        _draftRestored = true;
        await RestoreDraftAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (_window is not null)
        {
            _window.Stopped -= OnWindowStopped;
            _window = null;
        }
        SaveDraft();
    }

    private void OnWindowStopped(object? sender, EventArgs e) => SaveDraft();

    // Fire-and-forget: both triggers are synchronous callbacks, and the secure-storage
    // write lands well inside the window the OS gives a stopping app.
    private void SaveDraft()
    {
        if (_submitted)
            return;
        _ = _drafts.SaveAsync(CurrentDraft());
    }

    private CardiMemberDraft CurrentDraft() => new()
    {
        Name = NameEntry.Text,
        // Only a date the user actually chose counts as content — whatever the picker
        // reads back on an untouched form must not make an empty draft look filled in.
        DateOfBirth = _dobTouched ? DobPicker.Date : null,
        RelationshipIndex = RelationshipPicker.SelectedIndex,
        SexIndex = SexPicker.SelectedIndex,
        DetailsExpanded = DetailsSwitch.IsToggled,
        MedicalNotes = MedicalNotesEditor.Text,
        EmergencyContactName = EmergencyNameEntry.Text,
        EmergencyContactPhone = EmergencyPhoneEntry.Text,
        PhotoPath = _photoPath,
    };

    private async Task RestoreDraftAsync()
    {
        var draft = await _drafts.LoadAsync();
        // Never overwrite something the user has already started typing while we loaded.
        if (draft is null || CurrentDraft().HasContent)
            return;

        NameEntry.Text = draft.Name;
        if (draft.DateOfBirth is { } dob)
        {
            DobPicker.Date = dob;
            _dobTouched = true;
        }
        RelationshipPicker.SelectedIndex = draft.RelationshipIndex;
        SexPicker.SelectedIndex = draft.SexIndex;
        DetailsSwitch.IsToggled = draft.DetailsExpanded;
        MedicalNotesEditor.Text = draft.MedicalNotes;
        EmergencyNameEntry.Text = draft.EmergencyContactName;
        EmergencyPhoneEntry.Text = draft.EmergencyContactPhone;

        if (!string.IsNullOrEmpty(draft.PhotoPath))
        {
            _photoPath = draft.PhotoPath;
            PhotoImage.Source = ImageSource.FromFile(_photoPath);
            PhotoImage.IsVisible = true;
            PhotoPlaceholder.IsVisible = false;
        }

        // Setting Text/SelectedIndex raises the change handlers, but a draft holding only
        // a date of birth wouldn't — re-evaluate the CTA either way.
        OnFormChanged(this, EventArgs.Empty);
    }

    private async void OnBackRequested(object? sender, EventArgs e)
    {
        if (Navigation.NavigationStack.Count > 1)
            await Navigation.PopAsync();
        else
            await _ctx.CancelAsync(this);
    }

    private async void OnAddPhotoTapped(object? sender, EventArgs e)
    {
        var outcome = await MemberPhotoChooser.ShowAsync(_popups, offerRemove: _photoPath is not null);
        if (outcome.Removed)
        {
            // The file goes with the choice — a draft saved without its path would
            // otherwise strand it in app data forever.
            _drafts.RemovePhoto(_photoPath);
            _photoPath = null;
            PhotoImage.Source = null;
            PhotoImage.IsVisible = false;
            PhotoPlaceholder.IsVisible = true;
            SaveDraft();
            return;
        }

        if (outcome.Photo is not { } photo)
            return; // Cancelled the sheet or the picker — nothing changes.

        try
        {
            await using var picked = await photo.OpenReadAsync();
            var saved = await _drafts.CapturePhotoAsync(picked, _photoPath);
            if (saved is null)
            {
                // The durable copy failed; show the pick from the picker's own path for
                // this session — the draft store already tolerates that path vanishing on
                // restore. The previous draft file goes with the reference: nothing points
                // at it any more, so leaving it would strand it in app data.
                _drafts.RemovePhoto(_photoPath);
                saved = photo.FullPath;
            }
            _photoPath = saved;
            PhotoImage.Source = ImageSource.FromFile(_photoPath);
            PhotoImage.IsVisible = true;
            PhotoPlaceholder.IsVisible = false;
            // The picker/camera just backgrounded us — the likeliest moment to be killed.
            SaveDraft();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _popups.ShowWarningAsync("We couldn't read that photo. Try another one.");
        }
    }

    private void OnDetailsToggled(object? sender, ToggledEventArgs e) =>
        DetailsSection.IsVisible = e.Value;

    private void OnMedicalNotesChanged(object? sender, TextChangedEventArgs e)
    {
        MedicalNotesCounter.Text = $"{MedicalNotesEditor.Text?.Length ?? 0} / 500";
    }

    private void OnDobSelected(object? sender, DateChangedEventArgs e)
    {
        _dobTouched = true;
        OnFormChanged(sender, e);
    }

    private void OnFormChanged(object? sender, EventArgs e)
    {
        // Name and sex. Relationship is optional — an unpicked one is sent as "Other", so gating
        // Continue on it would make an optional field compulsory in everything but the label.
        //
        // Sex is gated, unlike every other field here, because an unanswered picker has nowhere
        // harmless to fall back to: it would store PreferNotToSay, which is exactly the state
        // this field exists to stop the whole population sitting in. A default that quietly
        // reproduces the bug is worse than one more tap.
        var name = NameEntry.Text?.Trim();
        ContinueBtn.IsEnabled =
            !string.IsNullOrWhiteSpace(name)
            && name.Length >= 2
            && SexPicker.SelectedIndex >= 0;
    }

    private async void OnContinueClicked(object? sender, EventArgs e)
    {
        FormError.IsVisible = false;
        ContinueBtn.Text = "Saving...";
        ContinueBtn.IsEnabled = false;

        try
        {
            var (photoBase64, abandoned) = await PreparePhotoAsync();
            if (abandoned)
                return; // They chose to go back and sort the photo out first.

            var member = await _api.CreateCardiMemberAsync(new CreateCardiMemberRequest
            {
                Name = NameEntry.Text!.Trim(),
                DateOfBirth = DateOnly.FromDateTime(DobPicker.Date ?? DateTime.Today),
                Gender = SelectedSex(),
                RelationshipType = SelectedRelationship(),
                MedicalNotes = NullIfEmpty(MedicalNotesEditor.Text),
                EmergencyContactName = NullIfEmpty(EmergencyNameEntry.Text),
                EmergencyContactPhone = NullIfEmpty(EmergencyPhoneEntry.Text),
                PhotoBase64 = photoBase64,
            });

            _submitted = true;
            await _drafts.ClearAsync();

            _ctx.Member = member;
            _ctx.MemberCreated = true;
            await Navigation.PushAsync(new DeviceSelectionPage(_ctx));
        }
        catch (ApiException ex)
        {
            FormError.Text = ex.Message;
            FormError.IsVisible = true;
        }
        finally
        {
            ContinueBtn.Text = "Continue";
            OnFormChanged(this, EventArgs.Empty);
        }
    }

    private async void OnSkipTapped(object? sender, EventArgs e) =>
        await _ctx.FinishAsync(this);

    /// <summary>
    /// The draft photo as the request's base64 payload, downscaled on device. A photo that
    /// can't be prepared must not cost the member: the server refuses to half-save a form
    /// with a bad photo, so the form offers to send itself without one instead —
    /// <c>abandoned</c> is true only when the user declines that offer.
    /// </summary>
    private async Task<(string? PhotoBase64, bool Abandoned)> PreparePhotoAsync()
    {
        if (_photoPath is null)
            return (null, false);

        ProfilePhotoUploadResult result;
        try
        {
            var original = await File.ReadAllBytesAsync(_photoPath);
            result = await ProfilePhotoUpload.PrepareAsync(original, _photoTranscoder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The draft file evaporated under us (OS cache pressure between sessions).
            result = ProfilePhotoUploadResult.Failed("We couldn't read the photo you added.");
        }

        if (result.Succeeded)
            return (result.Base64, false);

        var continueWithout = await _popups.ConfirmWarningAsync(
            $"{result.Error} You can add one later from their profile.",
            "That photo can't be uploaded",
            "Continue without photo",
            "Go back");
        return (null, !continueWithout);
    }

    /// <summary>
    /// Nothing picked means nothing stated, which is <see cref="RelationshipType.Other"/> — the
    /// same fallback the edit form uses, so the two screens agree about an unanswered picker.
    /// </summary>
    private RelationshipType SelectedRelationship() =>
        RelationshipPicker.SelectedIndex >= 0
            ? Relationships[RelationshipPicker.SelectedIndex].Value
            : RelationshipType.Other;

    /// <summary>
    /// The picked sex. <see cref="Gender.PreferNotToSay"/> is unreachable in practice — Continue
    /// stays disabled until something is picked — and is here only so a future change to that
    /// gating cannot turn an unanswered picker into an index-out-of-range at the point of submit.
    /// </summary>
    private Gender SelectedSex() =>
        SexPicker.SelectedIndex >= 0
            ? Sexes[SexPicker.SelectedIndex].Value
            : Gender.PreferNotToSay;

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
