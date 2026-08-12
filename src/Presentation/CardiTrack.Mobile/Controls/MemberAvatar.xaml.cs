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

    /// <summary>How wide the box is. Member Detail keeps the default; the dashboard hero widens it
    /// to stay in proportion with the taller box it stretches to.</summary>
    public double BoxWidth
    {
        get => Frame.WidthRequest;
        set => Frame.WidthRequest = value;
    }

    /// <summary>
    /// Lets the box grow to whatever height its row gives it, instead of staying square. The
    /// dashboard hero uses this so the bottom of the picture lands on the bottom of the text
    /// beside it — measured against the text rather than guessed at, so it stays aligned when a
    /// longer status line wraps or the caregiver runs a larger system font.
    /// </summary>
    public bool Stretch
    {
        get => Frame.VerticalOptions == LayoutOptions.Fill;
        set
        {
            Frame.VerticalOptions = value ? LayoutOptions.Fill : LayoutOptions.Start;
            // -1 is "no request" — without clearing it the 64 above would pin the height and the
            // Fill would have nothing to fill.
            Frame.HeightRequest = value ? -1 : BoxWidth;
        }
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
