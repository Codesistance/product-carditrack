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
[QueryProperty(nameof(FocusField), "focus")]
public partial class EditCardiMemberPage : ContentPage
{
    public const string Route = "editcardimember";

    /// <summary>Scrolls to and focuses the member phone field after the form loads.</summary>
    public const string FocusPhone = "phone";

    /// <summary>Scrolls to and focuses the emergency contact number after the form loads.</summary>
    public const string FocusEmergencyPhone = "emergency";

    /// <summary>
    /// Scrolls to the medical notes after the form loads. Used by the Medical Information page,
    /// whose whole subject is this one field — landing at the top of Basic Info would make the
    /// caregiver hunt for the thing they tapped Edit on.
    /// </summary>
    public const string FocusMedical = "medical";

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

    // Same two options as the M1-04 add form. PreferNotToSay is not offered here either: it is
    // where a member sits when nobody asked, and re-offering it as a choice would let a form
    // whose whole purpose is to fill that gap put it back.
    private static readonly (string Label, Gender Value)[] Sexes =
    [
        ("Male", Gender.Male),
        ("Female", Gender.Female),
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
    private string? _focusField;
    private CardiMemberDetailResponse? _member;
    private bool _isSaving;
    private bool _loaded;

    public EditCardiMemberPage(ICardiTrackApiClient api, IPopupService popups)
    {
        InitializeComponent();
        _api = api;
        _popups = popups;

        RelationshipPicker.ItemsSource = Relationships.Select(r => r.Label).ToList();
        SexPicker.ItemsSource = Sexes.Select(s => s.Label).ToList();
        SensitivityPicker.ItemsSource = Sensitivities.Select(s => s.Label).ToList();
        DobPicker.MaximumDate = DateTime.Today;
        DobPicker.MinimumDate = DateTime.Today.AddYears(-120);
        var phonePlaceholder = PhonePlaceholder.ForRegion(RegionInfo.CurrentRegion.TwoLetterISORegionName);
        EmergencyPhoneEntry.Placeholder = phonePlaceholder;
        PhoneEntry.Placeholder = phonePlaceholder;
    }

    public string MemberId
    {
        set => _memberId = Guid.TryParse(Uri.UnescapeDataString(value ?? string.Empty), out var id)
            ? id
            : Guid.Empty;
    }

    /// <summary>
    /// Optional field to land on — <see cref="FocusPhone"/> or <see cref="FocusEmergencyPhone"/>.
    /// Used when the caregiver came to fix one number, not to re-walk the whole profile.
    /// </summary>
    public string FocusField
    {
        set => _focusField = string.IsNullOrWhiteSpace(value)
            ? null
            : Uri.UnescapeDataString(value).Trim().ToLowerInvariant();
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
            await ApplyFocusAsync();
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
        PhoneEntry.Text = member.Phone;

        var relationshipIndex = Array.FindIndex(Relationships, r => r.Value == member.Relationship);
        // A relationship recorded before this picker existed (e.g. Child, Self) has no row —
        // fall back to "Other" rather than leaving the picker blank.
        RelationshipPicker.SelectedIndex = relationshipIndex >= 0
            ? relationshipIndex
            : Array.FindIndex(Relationships, r => r.Value == RelationshipType.Other);

        // Unlike relationship, an unmatched sex is left unselected rather than defaulted. The only
        // value that lands here is PreferNotToSay, which means nobody has been asked yet — and
        // picking a sex on the caregiver's behalf to avoid an empty control would put a guess
        // behind every summary written about this member.
        SexPicker.SelectedIndex = Array.FindIndex(Sexes, s => s.Value == member.Gender);

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

    /// <summary>
    /// Lands the caregiver on the field they came to change. The form is long enough that a
    /// phone-only fix otherwise starts at the top of Basic Info — they came here to type a
    /// number, so put the caret there.
    /// </summary>
    private async Task ApplyFocusAsync()
    {
        View? section = _focusField switch
        {
            FocusPhone => PhoneFieldSection,
            FocusEmergencyPhone => EmergencyPhoneFieldSection,
            FocusMedical => MedicalNotesFieldSection,
            _ => null,
        };
        if (section is null)
            return;

        // Layout has to finish before ScrollToAsync has bounds to aim at — without this the
        // scroll is a no-op on the first paint after SetState(form: true).
        await Task.Yield();
        await FormScroller.ScrollToAsync(section, ScrollToPosition.MakeVisible, animated: true);

        // VisualElement rather than Entry: the medical notes are a multi-line Editor, and both
        // carry Focus() from here.
        VisualElement? field = _focusField switch
        {
            FocusPhone => PhoneEntry,
            FocusEmergencyPhone => EmergencyPhoneEntry,
            FocusMedical => MedicalNotesEditor,
            _ => null,
        };
        field?.Focus();
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

        await NavigateBackToDetailAsync();
    }

    // Back to wherever the caregiver opened the form from — the dashboard's "add a number" offer
    // and the alerts list both push it directly, and returning them to Member Detail instead
    // would be a screen they never asked for. Every one of those callers refetches on appearing,
    // so a save lands the same either way. Member Detail is the floor for a push with nothing
    // behind it.
    private Task NavigateBackToDetailAsync() =>
        this.GoBackAsync($"{AppShell.DashboardRoute}/{CardiMemberDetailPage.Route}?memberId={_memberId}");

    private bool HasUnsavedChanges()
    {
        if (_member is null)
            return false;

        return NameEntry.Text?.Trim() != _member.Name
            || DateOnly.FromDateTime(DobPicker.Date ?? DateTime.Today) != _member.DateOfBirth
            // Only a picked sex can be a change. An untouched picker on a member with no sex
            // recorded is the state it loaded in, not an edit worth warning about on cancel.
            || SelectedSex() is { } sex && sex != _member.Gender
            || SelectedRelationship() != _member.Relationship
            || SelectedSensitivity() != _member.AlertSensitivity
            || NullIfEmpty(MedicalNotesEditor.Text) != NullIfEmpty(_member.MedicalNotes)
            || NullIfEmpty(EmergencyNameEntry.Text) != NullIfEmpty(_member.EmergencyContactName)
            || NullIfEmpty(EmergencyPhoneEntry.Text) != NullIfEmpty(_member.EmergencyContactPhone)
            || NullIfEmpty(PhoneEntry.Text) != NullIfEmpty(_member.Phone);
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
                Gender = SelectedSex(),
                RelationshipType = SelectedRelationship(),
                Email = _member.Email,
                Phone = NullIfEmpty(PhoneEntry.Text),
                EmergencyContactName = NullIfEmpty(EmergencyNameEntry.Text),
                EmergencyContactPhone = NullIfEmpty(EmergencyPhoneEntry.Text),
                MedicalNotes = NullIfEmpty(MedicalNotesEditor.Text),
                AlertSensitivity = SelectedSensitivity(),
            });

            // Detail refetches on appearing, so it picks these up without extra plumbing.
            await NavigateBackToDetailAsync();
        }
        catch (ApiException ex) when (!ex.IsSessionExpired)
        {
            await _popups.ShowErrorAsync(
                ex.Errors is { Count: > 0 } ? string.Join('\n', ex.Errors) : ex.Message,
                "Couldn't save these changes");
        }
        catch (ApiException)
        {
            // Session gone — the app is already on its way back to sign-in.
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
        PhoneError.IsVisible = false;

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

        var phone = NullIfEmpty(PhoneEntry.Text);
        if (phone is not null && !PhonePattern.IsMatch(phone))
        {
            PhoneError.Text = "Enter a valid phone number, e.g. +15551234567";
            PhoneError.IsVisible = true;
            valid = false;
        }

        return valid;
    }

    private RelationshipType SelectedRelationship() =>
        RelationshipPicker.SelectedIndex >= 0
            ? Relationships[RelationshipPicker.SelectedIndex].Value
            : RelationshipType.Other;

    /// <summary>
    /// The picked sex, or <c>null</c> for an untouched picker — which the API reads as "leave the
    /// stored value alone" rather than as a sex of none. Sex is not made compulsory on this form
    /// the way it is on M1-04: a caregiver opening the edit screen to fix a phone number should
    /// not be blocked behind a question about a member they may not have the answer for.
    /// </summary>
    private Gender? SelectedSex() =>
        SexPicker.SelectedIndex >= 0
            ? Sexes[SexPicker.SelectedIndex].Value
            : null;

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
