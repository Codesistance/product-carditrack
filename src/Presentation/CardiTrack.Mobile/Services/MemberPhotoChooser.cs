namespace CardiTrack.Mobile.Services;

/// <summary>
/// The CardiMember photo action sheet shared by the M1-04 add form and the M1-14 edit form,
/// so "what tapping the avatar offers" is one behaviour, not two. Runs the chooser and
/// whichever picker follows, and absorbs the platform's refusals into polite popups.
/// </summary>
public static class MemberPhotoChooser
{
    private const string TakeOption = "Take a photo";
    private const string LibraryOption = "Choose from library";
    private const string RemoveOption = "Remove photo";

    /// <summary>What came out of the sheet. At most one of the two is meaningful.</summary>
    /// <param name="Photo">The newly taken or picked photo, or null.</param>
    /// <param name="Removed">True when the user chose to remove the current photo.</param>
    public sealed record Outcome(FileResult? Photo, bool Removed)
    {
        /// <summary>Cancelled, dismissed, or the picker came back empty — change nothing.</summary>
        public bool IsNoOp => Photo is null && !Removed;
    }

    /// <param name="offerRemove">Whether a photo is currently set, making "remove" a real option.</param>
    public static async Task<Outcome> ShowAsync(IPopupService popups, bool offerRemove)
    {
        var options = new List<string>(3);
        // Hidden rather than disabled where there is no camera (tablets, desktops): an
        // option that can only apologise is not an option.
        if (MediaPicker.Default.IsCaptureSupported)
            options.Add(TakeOption);
        options.Add(LibraryOption);
        if (offerRemove)
            options.Add(RemoveOption);

        var choice = await popups.ChooseAsync("CardiMember photo", "Cancel", [.. options]);
        if (choice == RemoveOption)
            return new Outcome(Photo: null, Removed: true);

        try
        {
            var photo = choice switch
            {
                TakeOption => await MediaPicker.Default.CapturePhotoAsync(
                    new MediaPickerOptions { Title = "Take a photo" }),
                LibraryOption => (await MediaPicker.Default.PickPhotosAsync(
                    new MediaPickerOptions { Title = "Choose a photo" }))?.FirstOrDefault(),
                _ => null,
            };
            return new Outcome(photo, Removed: false);
        }
        catch (FeatureNotSupportedException)
        {
            await popups.ShowWarningAsync("This device can't take or pick photos.");
        }
        catch (PermissionException)
        {
            await popups.ShowWarningAsync(
                "Allow camera or photo access in Settings to add a photo.", "Permission needed");
        }
        return new Outcome(Photo: null, Removed: false);
    }
}
