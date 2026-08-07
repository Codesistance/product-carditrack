using System.Globalization;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Localization;
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

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;
    private readonly CardiMemberDraftStore _drafts;
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
        _ctx = ctx;

        if (ctx.Origin == WizardOrigin.Modal)
        {
            // Mid-flow entry: the onboarding "Step N of 4" story doesn't apply.
            Header.Step = string.Empty;
            Header.Progress = 0;
        }

        RelationshipPicker.ItemsSource = Relationships.Select(r => r.Label).ToList();
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
        try
        {
            var photos = await MediaPicker.Default.PickPhotosAsync(
                new MediaPickerOptions { Title = "Choose a photo" });
            var photo = photos?.FirstOrDefault();
            if (photo is null)
                return;

            await using var picked = await photo.OpenReadAsync();
            _photoPath = await _drafts.CapturePhotoAsync(picked, _photoPath) ?? photo.FullPath;
            PhotoImage.Source = ImageSource.FromFile(_photoPath);
            PhotoImage.IsVisible = true;
            PhotoPlaceholder.IsVisible = false;
            // The picker just backgrounded us — the likeliest moment to be killed.
            SaveDraft();
        }
        catch (FeatureNotSupportedException)
        {
            await _popups.ShowWarningAsync("Photo picking isn't supported on this device.");
        }
        catch (PermissionException)
        {
            await _popups.ShowWarningAsync(
                "Allow photo access in Settings to add a photo.", "Permission needed");
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
        var name = NameEntry.Text?.Trim();
        ContinueBtn.IsEnabled =
            !string.IsNullOrWhiteSpace(name) && name.Length >= 2 &&
            RelationshipPicker.SelectedIndex >= 0;
    }

    private async void OnContinueClicked(object? sender, EventArgs e)
    {
        FormError.IsVisible = false;
        ContinueBtn.Text = "Saving...";
        ContinueBtn.IsEnabled = false;

        try
        {
            var member = await _api.CreateCardiMemberAsync(new CreateCardiMemberRequest
            {
                Name = NameEntry.Text!.Trim(),
                DateOfBirth = DateOnly.FromDateTime(DobPicker.Date ?? DateTime.Today),
                Gender = Gender.PreferNotToSay,
                RelationshipType = Relationships[RelationshipPicker.SelectedIndex].Value,
                MedicalNotes = NullIfEmpty(MedicalNotesEditor.Text),
                EmergencyContactName = NullIfEmpty(EmergencyNameEntry.Text),
                EmergencyContactPhone = NullIfEmpty(EmergencyPhoneEntry.Text),
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

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
