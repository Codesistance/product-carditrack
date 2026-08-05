using System.Globalization;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Localization;
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
    private FileResult? _photo;

    public AddCardiMemberPage()
    {
        InitializeComponent();
        _api = ServiceHelper.GetRequiredService<ICardiTrackApiClient>();

        RelationshipPicker.ItemsSource = Relationships.Select(r => r.Label).ToList();
        DobPicker.MaximumDate = DateTime.Today;
        DobPicker.MinimumDate = DateTime.Today.AddYears(-120);
        EmergencyPhoneEntry.Placeholder = PhonePlaceholder.ForRegion(RegionInfo.CurrentRegion.TwoLetterISORegionName);
    }

    private async void OnBackRequested(object? sender, EventArgs e)
    {
        if (Navigation.NavigationStack.Count > 1)
            await Navigation.PopAsync();
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

            _photo = photo;
            PhotoImage.Source = ImageSource.FromFile(photo.FullPath);
            PhotoImage.IsVisible = true;
            PhotoPlaceholder.IsVisible = false;
        }
        catch (FeatureNotSupportedException)
        {
            await DisplayAlertAsync("Not available", "Photo picking isn't supported on this device.", "OK");
        }
        catch (PermissionException)
        {
            await DisplayAlertAsync("Permission needed", "Allow photo access in Settings to add a photo.", "OK");
        }
    }

    private void OnDetailsToggled(object? sender, ToggledEventArgs e) =>
        DetailsSection.IsVisible = e.Value;

    private void OnMedicalNotesChanged(object? sender, TextChangedEventArgs e)
    {
        MedicalNotesCounter.Text = $"{MedicalNotesEditor.Text?.Length ?? 0} / 500";
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

            await Navigation.PushAsync(new DeviceSelectionPage(member));
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

    private void OnSkipTapped(object? sender, EventArgs e) =>
        WindowNavigation.SetRootPage(this, new AppShell());

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
