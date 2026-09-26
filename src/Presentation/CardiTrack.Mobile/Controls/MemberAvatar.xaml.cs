using CardiTrack.Mobile.Services;
using CardiTrack.Mobile.Core.Members;

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

    /// <summary>How big the square box is. Member Detail sizes it to 80 against the three-line
    /// name / age / last-contact block; the dashboard hero uses 76.</summary>
    public double BoxWidth
    {
        get => Box.WidthRequest;
        set
        {
            // Always square: setting width alone would leave the old height behind it, and the
            // caller would get an oblong from what reads like a single size knob.
            Box.WidthRequest = value;
            Box.HeightRequest = value;
            InitialsLabel.FontSize = InitialsSize(value);
        }
    }

    /// <summary>
    /// The initials' share of the box: the same breathing room at every size the app draws one
    /// (40 in a roster row, 80 on Member Detail) — picked from samples (A1, 2026-09-26).
    /// </summary>
    private const double InitialsShare = 0.32;

    /// <summary>The initials' font size for a box of <paramref name="boxWidth"/>.</summary>
    public static double InitialsSize(double boxWidth) => Math.Round(boxWidth * InitialsShare);

    /// <param name="photoUrl">
    /// External data, so a relative or malformed value falls back to the initials rather than
    /// throwing the whole screen's load.
    /// </param>
    public void Apply(string? name, string? photoUrl)
    {
        InitialsLabel.Text = NameFormatting.Initials(name);

        var key = Uri.TryCreate(photoUrl, UriKind.Absolute, out var photoUri)
            ? MemberPhotoCacheKey.For(photoUri)
            : null;
        _photoKey = key;

        if (key is null)
        {
            PhotoImage.Source = null;
            PhotoImage.IsVisible = false;
            return;
        }

        // Already on the phone: shown at once, no download and no flash of initials. The signed
        // URL changes every few minutes; the photo behind it only when somebody changes it.
        if (MemberPhotoCache.Cached(key) is { } saved)
        {
            ShowPhoto(saved);
            return;
        }

        // Not yet: initials until it arrives. The same avatar may have been handed another member
        // by then (a recycled cell, a refresh), so only the photo still asked for is shown.
        PhotoImage.IsVisible = false;
        _ = LoadAsync(photoUri!, key);
    }

    private MemberPhotoCacheKey? _photoKey;

    private async Task LoadAsync(Uri url, MemberPhotoCacheKey key)
    {
        var path = await MemberPhotoCache.FetchAsync(url, key);
        if (path is null)
            return;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_photoKey == key)
                ShowPhoto(path);
        });
    }

    private void ShowPhoto(string path)
    {
        PhotoImage.Source = ImageSource.FromFile(path);
        PhotoImage.IsVisible = true;
    }
}
