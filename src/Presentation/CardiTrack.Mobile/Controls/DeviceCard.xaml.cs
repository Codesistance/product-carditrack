using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Services;
using CardiTrack.Mobile.Core.Devices;
using CardiTrack.Mobile.Core.Forms;
using CardiTrack.Mobile.Services;
using Microsoft.Maui.Controls.Shapes;

namespace CardiTrack.Mobile.Controls;

/// <summary>One connected wearable on M1-15, with its refresh / re-pull / primary / remove actions.</summary>
public partial class DeviceCard : ContentView
{
    public event EventHandler<Guid>? RefreshRequested;
    public event EventHandler<Guid>? RepullRequested;
    public event EventHandler<Guid>? SetPrimaryRequested;
    public event EventHandler<Guid>? RemoveRequested;

    /// <summary>The provider itself broke the token — carries the caregiver into M1-06 to redo consent.</summary>
    public event EventHandler<Guid>? ReconnectRequested;

    /// <summary>False while a re-pull is open or its cooldown is in force — the row then only reports.</summary>
    private bool _canRepull = true;

    /// <summary>Raised when the user opens a family's readings (the family) or folds them (null).</summary>
    public event EventHandler<DatasetFamily?>? SharingFamilyChanged;

    private Guid _deviceId;

    /// <summary>This card's device is the member's primary one — its star only reports.</summary>
    private bool _isPrimary;

    /// <summary>Guards the Toggled handler while <see cref="Apply"/> sets the switch itself.</summary>
    private bool _applying;

    /// <summary>What this device shares, by family, as last applied.</summary>
    private IReadOnlyList<DeviceDatasetGroup> _groups = [];

    /// <summary>The pills that open a strip — families with more than one reading.</summary>
    private readonly Dictionary<DatasetFamily, Border> _pills = [];

    /// <summary>The family whose readings the strip is showing, if any.</summary>
    private DatasetFamily? _openFamily;

    public DeviceCard()
    {
        InitializeComponent();
    }

    public void Apply(DeviceResponse device)
    {
        _applying = true;
        try
        {
            _deviceId = device.DeviceId;
            NameLabel.Text = device.DisplayName;
            ProviderImage.Source = ProviderImageFor(device.Provider);

            var (chipColour, textColour, label) = device.Status switch
            {
                "active" => ("#E3F7F0", "#1E8C6E", "ACTIVE"),
                "disconnected" => ("#FDE7E7", "#C42F2F", "DISCONNECTED"),
                _ => ("#FFF3DE", "#A9741A", "NEEDS RECONNECT"),
            };
            StatusChip.BackgroundColor = Color.FromArgb(chipColour);
            StatusLabel.TextColor = Color.FromArgb(textColour);
            StatusLabel.Text = label;

            // "token_expired" is the one wire status Refresh Connection cannot fix — the
            // provider itself rejected the refresh (expired grant, or the wearer revoked
            // access), so the identity/sharing/stats read as stale until the caregiver redoes
            // consent, and Refresh Connection gives way to Reconnect below. Re-pull History and
            // Set as Primary are withheld too — both would only queue against a connection that
            // cannot currently pull anything.
            var needsReconnect = device.Status == "token_expired";
            DeviceInfoSection.Opacity = needsReconnect ? 0.55 : 1;
            ReconnectButton.IsVisible = needsReconnect;
            RefreshButton.IsVisible = !needsReconnect;
            RepullButton.IsVisible = !needsReconnect;
            SemanticProperties.SetDescription(ReconnectButton,
                $"Reconnect {device.DisplayName}. Opens sign-in to restore the connection.");

            // One sentence for both ends of the sync: when it last sent, and when it next will.
            // The second half is left off while the connection cannot sync at all.
            var last = device.LastSyncedAt is { } synced
                ? $"Synced {RelativeTime.Format(synced)}"
                : "Not synced yet";
            SyncedLabel.Text = needsReconnect || device.NextSyncAt is null
                ? last
                : $"{last} · next {NextSyncText(device.NextSyncAt)}";

            ApplyDatasets(device.Scopes);

            ApplyBattery(device);
            ApplyHistoryRepull(device.HistoryRepull);

            // Filled on the primary; outlined, and a way to promote, on the others. Turning the
            // primary off would leave the member without one, so its star only reports. A
            // connection that needs reconnecting is offered no promotion: it is sending nothing.
            _isPrimary = device.IsPrimary;
            PrimaryStar.IsVisible = device.IsPrimary || !needsReconnect;
            PrimaryStarIcon.Source = device.IsPrimary ? "icon_star_on.svg" : "icon_star_off.svg";
            SemanticProperties.SetDescription(PrimaryStar, device.IsPrimary ? "Primary device" : "Make primary device");
            SemanticProperties.SetHint(PrimaryStar, device.IsPrimary
                ? "Its readings are used when two devices overlap"
                : "Double tap to use this device's readings when two overlap");
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>
    /// Shows the battery beside the device name, but only when the server sent a reading. It
    /// withholds one whenever the connection never granted the settings scope, the hardware has
    /// no battery, or the last reading has aged out — so "no battery field" means "nothing
    /// trustworthy to say", and an empty or guessed badge is never drawn in its place.
    /// </summary>
    private void ApplyBattery(DeviceResponse device)
    {
        var text = device.BatteryLevel is { } level
            ? $"{level}%"
            : device.BatteryStatus;

        BatteryBadge.IsVisible = !string.IsNullOrWhiteSpace(text);
        if (!BatteryBadge.IsVisible)
            return;

        BatteryValue.Text = text;

        // Red at the same threshold DEVICE_BATTERY_LOW fires at, so the badge and the notification
        // a caregiver may already have received agree about what counts as low.
        var low = DeviceBattery.IsLow(device.BatteryLevel, device.BatteryStatus);
        BatteryValue.TextColor = low ? Color.FromArgb("#C42F2F") : Color.FromArgb("#1E8C6E");
        BatteryIcon.Source = low
            ? "icon_battery_low.svg"
            : device.BatteryLevel is >= 60 ? "icon_battery_full.svg" : "icon_battery_half.svg";
        SemanticProperties.SetDescription(BatteryBadge, $"Battery {text}{(low ? ", low" : string.Empty)}");
    }

    /// <summary>
    /// The re-pull row: the action while one may be requested, and the request's progress line
    /// underneath whenever the server sent one. A card mid-request keeps the row but stops
    /// offering it — tapping would only earn a 409 — and says so to a screen reader.
    /// </summary>
    private void ApplyHistoryRepull(DeviceHistoryRepullResponse? repull)
    {
        var now = DateTime.UtcNow;
        _canRepull = HistoryRepullCopy.CanRequest(repull, now);

        var status = HistoryRepullCopy.StatusLine(repull, now);
        RepullStatusLabel.Text = status ?? string.Empty;
        RepullStatusLabel.IsVisible = status is not null;
        // Genuinely not a control while withheld, rather than a tap that silently does nothing:
        // a disabled button is announced as such by a screen reader, and dimmed for everyone.
        RepullButton.IsEnabled = _canRepull;

        SemanticProperties.SetDescription(RepullButton, status is null
            ? "Re-pull history"
            : $"Re-pull history. {status}");
        SemanticProperties.SetHint(RepullButton, _canRepull
            ? "Re-reads past days from the device's provider to fill gaps"
            : string.Empty);
    }

    /// <summary>Disables the actions while a request for this card is in flight.</summary>
    public void SetBusy(bool busy)
    {
        IsEnabled = !busy;
        RefreshButton.Text = busy ? "Working…" : "Refresh";
    }

    private static string NextSyncText(DateTime? nextSyncAt)
    {
        if (nextSyncAt is not { } next)
            return "when connected";

        var minutes = (int)Math.Ceiling(
            (DateTime.SpecifyKind(next, DateTimeKind.Utc) - DateTime.UtcNow).TotalMinutes);
        return minutes switch
        {
            <= 0 => "any moment",
            1 => "in 1 min",
            < 60 => $"in {minutes} mins",
            _ => $"in {minutes / 60}h",
        };
    }

    /// <summary>
    /// Rebuilds the sharing row: one pill per dataset family, each that stands for several readings
    /// opening them in the strip below it. A connection sharing nothing is worth saying out loud —
    /// it looks connected but sends no data — so the row keeps a pill either way.
    /// </summary>
    private void ApplyDatasets(List<string> scopes)
    {
        DatasetPills.Children.Clear();
        _pills.Clear();
        _groups = DeviceDatasets.GroupedFor(scopes);

        if (_groups.Count == 0)
        {
            SharingLabel.Text = "SHARING";
            DatasetPills.Children.Add(BuildWarningPill("Not sharing any data"));
            SetOpenFamily(null);
            return;
        }

        var readings = _groups.Sum(g => g.Datasets.Count);
        SharingLabel.Text = readings == 1 ? "SHARING · 1 READING" : $"SHARING · {readings} READINGS";

        foreach (var group in _groups)
        {
            var pill = BuildPill(group);
            DatasetPills.Children.Add(pill);

            // A pill that names its one reading already says everything it could open.
            if (group.Datasets.Count > 1)
            {
                _pills[group.Family] = pill;
                var tap = new TapGestureRecognizer();
                tap.Tapped += (_, _) => OnPillTapped(group.Family);
                pill.GestureRecognizers.Add(tap);
            }
        }

        // Re-applied so a rebuild keeps (or drops, if that family has gone) whatever was open.
        SetOpenFamily(_openFamily);
    }

    /// <summary>
    /// A family pill: the label, plus the number of readings when the family carries several.
    /// The count keeps "how much" visible while "which ones" waits behind a tap.
    /// </summary>
    private static Border BuildPill(DeviceDatasetGroup group)
    {
        var (background, foreground) = PillColours(group.Family);

        var text = new FormattedString();
        text.Spans.Add(new Span
        {
            Text = group.Label,
            TextColor = foreground,
            FontFamily = "QuicksandSemiBold",
            FontSize = 11,
        });

        if (group.Count is { } count)
        {
            text.Spans.Add(new Span
            {
                Text = $"  {count}",
                TextColor = foreground,
                FontFamily = "Quicksand",
                FontSize = 11,
            });
        }

        // The family's glyph in the pill's own ink, so the row reads at a glance — a heart, a
        // moon, a runner — before the words are read. Decorative: the pill's description says it.
        var pill = Pill(background, new HorizontalStackLayout
        {
            Spacing = 5,
            Children =
            {
                new Image
                {
                    Source = IconFor(group.Family),
                    WidthRequest = 14,
                    HeightRequest = 14,
                    VerticalOptions = LayoutOptions.Center,
                },
                new Label { FormattedText = text, VerticalOptions = LayoutOptions.Center },
            },
        });

        // "Activity  5" is only unambiguous once you can see the colour grouping; spell it out
        // for a screen reader, which gets the pills one after another with no row to compare.
        SemanticProperties.SetDescription(
            pill,
            group.Count is { } n
                ? $"{group.Label}, {n} readings: {group.Detail}"
                : $"{group.Label}, 1 reading");

        return pill;
    }

    private static Border BuildWarningPill(string text)
    {
        var resources = Microsoft.Maui.Controls.Application.Current!.Resources;

        return Pill((Color)resources["DatasetWarningBackground"], new Label
        {
            Text = text,
            TextColor = (Color)resources["DatasetWarningText"],
            FontFamily = "QuicksandSemiBold",
            FontSize = 11,
        });
    }

    private static string IconFor(DatasetFamily family) => family switch
    {
        DatasetFamily.Activity => "icon_dataset_activity.svg",
        DatasetFamily.Heart => "icon_dataset_heart.svg",
        DatasetFamily.Sleep => "icon_dataset_sleep.svg",
        DatasetFamily.Body => "icon_dataset_body.svg",
        _ => "icon_dataset_other.svg",
    };

    /// <remarks>
    /// Always carries a 1.5 stroke, transparent until the pill is the open one: a stroke that
    /// appeared only on selection would widen the pill and reflow the row under the finger.
    /// </remarks>
    private static Border Pill(Color background, View content) => new()
    {
        StrokeThickness = 1.5,
        Stroke = Colors.Transparent,
        BackgroundColor = background,
        Padding = new Thickness(10, 4),
        // FlexLayout has no spacing of its own; the margin is the gutter between pills.
        Margin = new Thickness(0, 0, 6, 6),
        StrokeShape = new RoundRectangle { CornerRadius = 10 },
        Content = content,
    };

    /// <summary>Resolves a family's tint/ink pair from the Colors.xaml palette.</summary>
    private static (Color Background, Color Foreground) PillColours(DatasetFamily family)
    {
        var token = family switch
        {
            DatasetFamily.Activity => "DatasetActivity",
            DatasetFamily.Heart => "DatasetHeart",
            DatasetFamily.Sleep => "DatasetSleep",
            DatasetFamily.Body => "DatasetBody",
            _ => "DatasetOther",
        };

        var resources = Microsoft.Maui.Controls.Application.Current!.Resources;
        return ((Color)resources[$"{token}Background"], (Color)resources[$"{token}Text"]);
    }

    private void OnPillTapped(DatasetFamily family)
    {
        var open = _openFamily == family ? (DatasetFamily?)null : family;
        SetOpenFamily(open);
        SharingFamilyChanged?.Invoke(this, open);
    }

    /// <summary>
    /// Opens <paramref name="family"/>'s readings in the strip under the pills, or folds the strip
    /// for <c>null</c>. Also how the page restores what was open after a reload: it re-creates
    /// every card on refresh, so without this an open strip would snap shut each time an action
    /// reloads the list. A family this device no longer shares (or one with a single reading,
    /// which has nothing to open) folds the strip.
    /// </summary>
    public void SetOpenFamily(DatasetFamily? family)
    {
        var group = family is { } f && _pills.ContainsKey(f)
            ? _groups.FirstOrDefault(g => g.Family == f)
            : null;
        _openFamily = group?.Family;

        foreach (var (pillFamily, pill) in _pills)
        {
            var isOpen = pillFamily == _openFamily;
            pill.Stroke = isOpen ? PillColours(pillFamily).Foreground : Colors.Transparent;
            SemanticProperties.SetHint(pill, isOpen
                ? "Double tap to hide its readings"
                : "Double tap to list its readings");
        }

        SharingStrip.IsVisible = group is not null;
        if (group is null)
            return;

        var (background, foreground) = PillColours(group.Family);
        SharingStrip.BackgroundColor = background;
        SharingStripLabel.TextColor = foreground;
        SharingStripLabel.Text = group.Detail;
    }

    private static string ProviderImageFor(string provider) => provider.ToLowerInvariant() switch
    {
        "fitbit" => "device_fitbit.png",
        // Placeholder until design ships a Pixel Watch asset.
        "pixel_watch" => "device_other.png",
        "garmin" => "device_garmin.png",
        "samsung_health" => "device_samsung.png",
        "withings" => "device_withings.png",
        "apple_health" => "device_apple_watch.png",
        _ => "device_other.png",
    };

    private void OnRefreshClicked(object? sender, EventArgs e) =>
        RefreshRequested?.Invoke(this, _deviceId);

    private void OnReconnectClicked(object? sender, EventArgs e) =>
        ReconnectRequested?.Invoke(this, _deviceId);

    private void OnRepullClicked(object? sender, EventArgs e)
    {
        if (!_canRepull)
            return;
        RepullRequested?.Invoke(this, _deviceId);
    }

    private void OnRemoveClicked(object? sender, EventArgs e) =>
        RemoveRequested?.Invoke(this, _deviceId);

    private void OnPrimaryStarTapped(object? sender, TappedEventArgs e)
    {
        if (_applying || _isPrimary)
            return;
        SetPrimaryRequested?.Invoke(this, _deviceId);
    }
}
