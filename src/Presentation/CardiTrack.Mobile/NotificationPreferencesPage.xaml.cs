using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

public partial class NotificationPreferencesPage : ContentPage
{
    public const string Route = "notificationpreferences";

    /// <summary>
    /// The categories a caregiver may mute, in the order they appear, with the words a caregiver
    /// would use for them. Safety is listed too — pinned on — so the row says what cannot be
    /// silenced instead of leaving that to be discovered. The wire values are the enum names
    /// (<c>NotificationCategory</c>), which is what the API's <c>mutedCategories</c> carries.
    /// </summary>
    private static readonly (string Wire, string Label, string Detail, bool CanMute)[] Categories =
    [
        ("Safety", "Safety", "A device gone quiet, or nobody listening. Can't be muted.", false),
        ("Blocking", "Setup reminders", "Something is stopping CardiTrack working — no device, a baseline still learning", true),
        ("Unlock", "Tips", "Things you could add to get more from their readings", true),
        ("Account", "Account notices", "Sign-in, trial and account changes", true),
    ];

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;
    private readonly Dictionary<string, Switch> _categorySwitches = new(StringComparer.Ordinal);

    private NotificationPreferenceResponse? _prefs;
    private bool _rendering;
    private bool _saving;

    public NotificationPreferencesPage(ICardiTrackApiClient api, IPopupService popups)
    {
        InitializeComponent();
        _api = api;
        _popups = popups;
        BuildCategoryRows();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = LoadAsync();
    }

    private async void OnBackTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToAsync("..");

    private async void OnRetryClicked(object? sender, EventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        Loading.IsVisible = true;
        Panel.IsVisible = false;
        ErrorPanel.IsVisible = false;
        try
        {
            _prefs = await _api.GetNotificationPreferencesAsync();
            Render();
            Panel.IsVisible = true;
        }
        catch (ApiException)
        {
            ErrorPanel.IsVisible = true;
        }
        finally
        {
            Loading.IsVisible = false;
        }
    }

    private void Render()
    {
        if (_prefs is null)
            return;

        // Switches raise Toggled when set from code too; the flag keeps a render from saving.
        _rendering = true;
        try
        {
            QuietHoursValue.Text = _prefs.QuietHoursStart is { } start && _prefs.QuietHoursEnd is { } end
                ? $"{start:HH:mm} – {end:HH:mm}"
                : "Off";
            LockScreenSwitch.IsToggled = _prefs.ShowDetailsOnLockScreen;
            foreach (var (wire, sw) in _categorySwitches)
                sw.IsToggled = !_prefs.MutedCategories.Contains(wire, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _rendering = false;
        }
    }

    private void BuildCategoryRows()
    {
        foreach (var (wire, label, detail, canMute) in Categories)
        {
            var row = new Grid
            {
                ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
                ColumnSpacing = 12,
                MinimumHeightRequest = 56,
                Padding = new Thickness(0, 6),
            };
            var resources = Microsoft.Maui.Controls.Application.Current!.Resources;
            var text = new VerticalStackLayout { Spacing = 1, VerticalOptions = LayoutOptions.Center };
            text.Add(new Label { Text = label, Style = (Style)resources["Body1SemiBoldDark"] });
            text.Add(new Label { Text = detail, Style = (Style)resources["Body2"], LineBreakMode = LineBreakMode.WordWrap });
            row.Add(text, 0, 0);

            var sw = new Switch
            {
                OnColor = (Color)Microsoft.Maui.Controls.Application.Current!.Resources["Primary"],
                VerticalOptions = LayoutOptions.Center,
                IsToggled = true,
                IsEnabled = canMute,
                // A pinned-on switch still reads as a switch; dimming says why it does not move.
                Opacity = canMute ? 1 : 0.5,
            };
            SemanticProperties.SetDescription(sw, canMute ? $"Hear about {label.ToLowerInvariant()}" : "Safety notifications, always on");
            if (canMute)
                sw.Toggled += (_, e) => _ = OnCategoryToggledAsync(wire, e.Value);
            row.Add(sw, 1, 0);

            _categorySwitches[wire] = sw;
            CategoryRows.Add(row);
        }
    }

    private async void OnQuietHoursTapped(object? sender, TappedEventArgs e)
    {
        if (_prefs is null || _saving)
            return;

        // Two questions rather than a range picker: "from when" then "until when", each an hour
        // on the hour, with Off as the way out. Turning them off asks nothing further.
        var startChoice = await _popups.ChooseAsync("Quiet from", "Cancel", ["Off", .. Hours()]);
        if (startChoice is null)
            return;

        if (startChoice == "Off")
        {
            await SaveAsync(p => { p.QuietHoursStart = null; p.QuietHoursEnd = null; });
            return;
        }

        var endChoice = await _popups.ChooseAsync("Quiet until", "Cancel", Hours());
        if (endChoice is null)
            return;

        var start = TimeOnly.ParseExact(startChoice, "HH:mm");
        var end = TimeOnly.ParseExact(endChoice, "HH:mm");
        await SaveAsync(p => { p.QuietHoursStart = start; p.QuietHoursEnd = end; });
    }

    private static string[] Hours() =>
        [.. Enumerable.Range(0, 24).Select(h => new TimeOnly(h, 0).ToString("HH:mm"))];

    private async void OnLockScreenToggled(object? sender, ToggledEventArgs e)
    {
        if (_rendering || _prefs is null)
            return;
        await SaveAsync(p => p.ShowDetailsOnLockScreen = e.Value);
    }

    private async Task OnCategoryToggledAsync(string wire, bool hear)
    {
        if (_rendering || _prefs is null)
            return;
        await SaveAsync(p =>
        {
            p.MutedCategories.RemoveAll(c => string.Equals(c, wire, StringComparison.OrdinalIgnoreCase));
            if (!hear)
                p.MutedCategories.Add(wire);
        });
    }

    /// <summary>
    /// Sends the whole preferences object with one change applied — the PUT replaces, so a
    /// caller that omitted a field would silently reset it. On failure the screen goes back to
    /// what the server has, so a switch never shows a state that did not save.
    /// </summary>
    private async Task SaveAsync(Action<UpdateNotificationPreferenceRequest> change)
    {
        if (_prefs is null)
            return;

        // A toggle flipped while a save is in flight has already moved on screen; snap it back
        // to what the server holds rather than let it look saved until the other save returns.
        if (_saving)
        {
            Render();
            return;
        }

        var request = new UpdateNotificationPreferenceRequest
        {
            QuietHoursStart = _prefs.QuietHoursStart,
            QuietHoursEnd = _prefs.QuietHoursEnd,
            ShowDetailsOnLockScreen = _prefs.ShowDetailsOnLockScreen,
            MutedCategories = [.. _prefs.MutedCategories],
        };
        change(request);

        _saving = true;
        try
        {
            _prefs = await _api.UpdateNotificationPreferencesAsync(request);
        }
        catch (ApiException ex)
        {
            await _popups.ShowWarningAsync(ex.Message, "Couldn't save");
        }
        finally
        {
            _saving = false;
            Render();
        }
    }
}
