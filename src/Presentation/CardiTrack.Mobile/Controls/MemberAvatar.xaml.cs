using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// A CardiMember's display image: their photo when there is one, their initials when there
/// isn't. Shared by the dashboard hero card and Member Detail so both screens show the same
/// person the same way.
/// </summary>
public partial class MemberAvatar : ContentView
{
    public MemberAvatar()
    {
        InitializeComponent();
    }

    /// <param name="photoUrl">
    /// External data, so a relative or malformed value falls back to the initials rather than
    /// throwing the whole screen's load.
    /// </param>
    public void Apply(string? name, string? photoUrl)
    {
        InitialsLabel.Text = NameFormatting.Initials(name);

        var hasPhoto = Uri.TryCreate(photoUrl, UriKind.Absolute, out var photoUri);
        PhotoImage.Source = hasPhoto ? ImageSource.FromUri(photoUri!) : null;
        PhotoImage.IsVisible = hasPhoto;
    }
}
