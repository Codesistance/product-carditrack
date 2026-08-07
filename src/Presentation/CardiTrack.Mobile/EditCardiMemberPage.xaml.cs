using System.Globalization;
using System.Text.RegularExpressions;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Localization;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

/// <summary>M1-14 Edit CardiMember. Entered from the M1-13 detail screen's edit button.</summary>
[QueryProperty(nameof(MemberId), "memberId")]
public partial class EditCardiMemberPage : ContentPage
{
    public const string Route = "editcardimember";

    // Same order and labels as the M1-04 add form, so a member's relationship doesn't
    // appear to change wording between the two screens.
    private static readonly (string Label, RelationshipType Value)[] Relationships =
    [
        ("Parent", RelationshipType.Parent),
        ("Grandparent", RelationshipType.Grandparent),
        ("Spouse", RelationshipType.Spouse),
        ("Sibling", RelationshipType.Sibling),
        ("Other", RelationshipType.Other),
    ];

    private static readonly (string Label, AlertSensitivity Value)[] Sensitivities =
    [
        ("Low", AlertSensitivity.Low),
        ("Medium", AlertSensitivity.Medium),
        ("High", AlertSensitivity.High),
    ];

    // Matches the server's phone rule so the form rejects what the API would reject.
    private static readonly Regex PhonePattern = new(@"^\+?[1-9]\d{1,14}$", RegexOptions.Compiled);

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;

    private Guid _memberId;
    private CardiMemberDetailResponse? _member;
    private bool _isSaving;
    private bool _loaded;

    public EditCardiMemberPage(ICardiTrackApiClient api, IPopupService popups)
    {
        InitializeComponent();
        _api = api;
        _popups = popups;

        RelationshipPicker.ItemsSource = Relationships.Select(r => r.Label).ToList();
        SensitivityPicker.ItemsSource = Sensitivities.Select(s => s.Label).ToList();
        DobPicker.MaximumDate = DateTime.Today;
        DobPicker.MinimumDate = DateTime.Today.AddYears(-120);
        EmergencyPhoneEntry.Placeholder =
            PhonePlaceholder.ForRegion(RegionInfo.CurrentRegion.TwoLetterISORegionName);
    }

    public string MemberId
    {
        set => _memberId = Guid.TryParse(Uri.UnescapeDataString(value ?? string.Empty), out var id)
            ? id
            : Guid.Empty;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // Load once: re-fetching would discard whatever the user has typed if the OS
        // brings this page back to the foreground mid-edit.
        if (_loaded)
            return;
        _loaded = true;
        _ = LoadAsync();
    }

    private void OnRetryClicked(object? sender, EventArgs e) => _ = LoadAsync();

    private async Task LoadAsync()
    {
        SetState(loading: true);
        try
        {
            _member = await _api.GetCardiMemberAsync(_memberId);
            Fill(_member);
            SetState(form: true);
        }
        catch (ApiException ex)
        {
            ErrorDetailLabel.Text = ex.Message;
            SetState(error: true);
        }
    }

    private void Fill(CardiMemberDetailResponse member)
    {
        InitialsLabel.Text = NameFormatting.Initials(member.Name);
        NameEntry.Text = member.Name;
        DobPicker.Date = member.DateOfBirth.ToDateTime(TimeOnly.MinValue);
        MedicalNotesEditor.Text = member.MedicalNotes;
        EmergencyNameEntry.Text = member.EmergencyContactName;
        EmergencyPhoneEntry.Text = member.EmergencyContactPhone;

        var relationshipIndex = Array.FindIndex(Relationships, r => r.Value == member.Relationship);
        // A relationship recorded before this picker existed (e.g. Child, Self) has no row —
        // fall back to "Other" rather than leaving the picker blank.
        RelationshipPicker.SelectedIndex = relationshipIndex >= 0
            ? relationshipIndex
            : Array.FindIndex(Relationships, r => r.Value == RelationshipType.Other);

        SensitivityPicker.SelectedIndex = Math.Max(
            0, Array.FindIndex(Sensitivities, s => s.Value == member.AlertSensitivity));
    }

    private void SetState(bool loading = false, bool form = false, bool error = false)
    {
        SkeletonPanel.IsVisible = loading;
        FormPanel.IsVisible = form;
        ErrorPanel.IsVisible = error;
        SaveButton.IsVisible = form;
    }

    /// <summary>Keeps the avatar in step with the name as it is typed.</summary>
    private void OnNameChanged(object? sender, TextChangedEventArgs e) =>
        InitialsLabel.Text = NameFormatting.Initials(NameEntry.Text);

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        if (HasUnsavedChanges())
        {
            var discard = await _popups.ConfirmWarningAsync(
                "Your changes to this profile haven't been saved.",
                "Discard changes?",
                "Discard",
                "Keep editing");
            if (!discard)
                return;
        }

        await Shell.Current.GoToAsync("..");
    }

    private bool HasUnsavedChanges()
    {
        if (_member is null)
            return false;

        return NameEntry.Text?.Trim() != _member.Name
            || DateOnly.FromDateTime(DobPicker.Date ?? DateTime.Today) != _member.DateOfBirth
            || SelectedRelationship() != _member.Relationship
            || SelectedSensitivity() != _member.AlertSensitivity
            || NullIfEmpty(MedicalNotesEditor.Text) != NullIfEmpty(_member.MedicalNotes)
            || NullIfEmpty(EmergencyNameEntry.Text) != NullIfEmpty(_member.EmergencyContactName)
            || NullIfEmpty(EmergencyPhoneEntry.Text) != NullIfEmpty(_member.EmergencyContactPhone);
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        if (_isSaving || _member is null)
            return;

        if (!Validate())
            return;

        _isSaving = true;
        SaveButton.Text = "Saving...";
        SaveButton.IsEnabled = false;

        try
        {
            await _api.UpdateCardiMemberAsync(_memberId, new UpdateCardiMemberRequest
            {
                Name = NameEntry.Text!.Trim(),
                DateOfBirth = DateOnly.FromDateTime(DobPicker.Date ?? DateTime.Today),
                RelationshipType = SelectedRelationship(),
                Email = _member.Email,
                Phone = _member.Phone,
                EmergencyContactName = NullIfEmpty(EmergencyNameEntry.Text),
                EmergencyContactPhone = NullIfEmpty(EmergencyPhoneEntry.Text),
                MedicalNotes = NullIfEmpty(MedicalNotesEditor.Text),
                AlertSensitivity = SelectedSensitivity(),
            });

            // Detail refetches on appearing, so it picks these up without extra plumbing.
            await Shell.Current.GoToAsync("..");
        }
        catch (ApiException ex)
        {
            await _popups.ShowErrorAsync(
                ex.Errors is { Count: > 0 } ? string.Join('\n', ex.Errors) : ex.Message,
                "Couldn't save these changes");
        }
        finally
        {
            _isSaving = false;
            SaveButton.Text = "Save";
            SaveButton.IsEnabled = true;
        }
    }

    private bool Validate()
    {
        NameError.IsVisible = false;
        DobError.IsVisible = false;
        MedicalNotesError.IsVisible = false;
        EmergencyPhoneError.IsVisible = false;

        var valid = true;

        var name = NameEntry.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length is < 2 or > 100)
        {
            NameError.Text = "Name must be between 2 and 100 characters";
            NameError.IsVisible = true;
            valid = false;
        }

        var age = AgeOn(DateOnly.FromDateTime(DobPicker.Date ?? DateTime.Today));
        if (age is < 18 or > 120)
        {
            DobError.Text = "CardiMember must be between 18 and 120 years old";
            DobError.IsVisible = true;
            valid = false;
        }

        if ((MedicalNotesEditor.Text?.Length ?? 0) > 2000)
        {
            MedicalNotesError.Text = "Medical notes cannot exceed 2000 characters";
            MedicalNotesError.IsVisible = true;
            valid = false;
        }

        var emergencyPhone = NullIfEmpty(EmergencyPhoneEntry.Text);
        if (emergencyPhone is not null && !PhonePattern.IsMatch(emergencyPhone))
        {
            EmergencyPhoneError.Text = "Enter a valid phone number, e.g. +15551234567";
            EmergencyPhoneError.IsVisible = true;
            valid = false;
        }

        return valid;
    }

    private RelationshipType SelectedRelationship() =>
        RelationshipPicker.SelectedIndex >= 0
            ? Relationships[RelationshipPicker.SelectedIndex].Value
            : RelationshipType.Other;

    private AlertSensitivity SelectedSensitivity() =>
        SensitivityPicker.SelectedIndex >= 0
            ? Sensitivities[SensitivityPicker.SelectedIndex].Value
            : AlertSensitivity.Medium;

    private static int AgeOn(DateOnly dateOfBirth)
    {
        var today = DateTime.UtcNow;
        var age = today.Year - dateOfBirth.Year;
        if (today.Month < dateOfBirth.Month || (today.Month == dateOfBirth.Month && today.Day < dateOfBirth.Day))
            age--;
        return age;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
