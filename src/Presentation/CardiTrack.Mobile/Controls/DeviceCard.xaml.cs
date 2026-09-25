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

    /// <summary>Raised with the new state when the user opens or closes the sharing detail.</summary>
    public event EventHandler<bool>? SharingExpansionChanged;

    private Guid _deviceId;

    /// <summary>Guards the Toggled handler while <see cref="Apply"/> sets the switch itself.</summary>
    private bool _applying;

    /// <summary>False when every family names a single dataset — the pills already say it all.</summary>
    private bool _canExpandSharing;

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
            PrimaryRow.IsVisible = !needsReconnect;
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

            TodayValue.Text = device.TodayUpdateCount switch
            {
                0 => "No updates",
                1 => "1 update",
                var n => $"{n} updates",
            };

            ApplyBattery(device);
            ApplyHistoryRepull(device.HistoryRepull);

            PrimaryPill.IsVisible = device.IsPrimary;
            PrimarySwitch.IsToggled = device.IsPrimary;
            // Turning the only primary off would leave the member without one; promotion
            // happens by switching a different device on.
            PrimarySwitch.IsEnabled = !device.IsPrimary;
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>
    /// Shows the battery tile, but only when the server sent a reading. It withholds one whenever
    /// the connection never granted the settings scope, the hardware has no battery, or the last
    /// reading has aged out — so "no battery field" means "nothing trustworthy to say", and an
    /// empty or guessed tile is never drawn in its place.
    /// </summary>
    /// <remarks>
    /// A hidden child does not release its grid column, so the column width is collapsed alongside
    /// it; otherwise the three remaining tiles would sit in three quarters of the row with a gap
    /// where the fourth belongs.
    /// </remarks>
    private void ApplyBattery(DeviceResponse device)
    {
        var text = device.BatteryLevel is { } level
            ? $"{level}%"
            : device.BatteryStatus;

        var hasReading = !string.IsNullOrWhiteSpace(text);

        BatteryTile.IsVisible = hasReading;
        BatteryColumn.Width = hasReading ? GridLength.Star : new GridLength(0);

        if (!hasReading)
            return;

        BatteryValue.Text = text;

        // Red at the same threshold DEVICE_BATTERY_LOW fires at, so the tile and the notification
        // a caregiver may already have received agree about what counts as low.
        BatteryValue.TextColor = DeviceBattery.IsLow(device.BatteryLevel, device.BatteryStatus)
            ? Color.FromArgb("#C42F2F")
            : Color.FromArgb("#1E8C6E");
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
        RepullButton.Opacity = _canRepull ? 1 : 0.5;

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
    /// Rebuilds the sharing row: one pill per dataset family, with the family's readings on a
    /// detail line behind the chevron. A connection sharing nothing is worth saying out loud —
    /// it looks connected but sends no data — so the row keeps a pill either way.
    /// </summary>
    private void ApplyDatasets(List<string> scopes)
    {
        DatasetPills.Children.Clear();
        SharingDetail.Children.Clear();

        var groups = DeviceDatasets.GroupedFor(scopes);
        if (groups.Count == 0)
        {
            DatasetPills.Children.Add(BuildWarningPill("Not sharing any data"));
            SetSharingExpandable(false);
            return;
        }

        foreach (var group in groups)
        {
            DatasetPills.Children.Add(BuildPill(group));
            SharingDetail.Children.Add(BuildDetailLine(group));
        }

        // Nothing to reveal when every pill already names its one dataset.
        SetSharingExpandable(groups.Any(g => g.Datasets.Count > 1));
    }

    /// <summary>
    /// A family pill: the label, plus the number of readings when the family carries several.
    /// The count is the whole point of collapsing the row — it keeps "how much" visible after
    /// "which ones" moves behind the chevron.
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

    private static Border Pill(Color background, View content) => new()
    {
        StrokeThickness = 0,
        BackgroundColor = background,
        Padding = new Thickness(10, 4),
        // FlexLayout has no spacing of its own; the margin is the gutter between pills.
        Margin = new Thickness(0, 0, 6, 6),
        StrokeShape = new RoundRectangle { CornerRadius = 10 },
        Content = content,
    };

    /// <summary>"Heart  Heart Rate · Resting HR" — the family in its own ink, the readings after.</summary>
    private static Label BuildDetailLine(DeviceDatasetGroup group)
    {
        var (_, foreground) = PillColours(group.Family);

        var text = new FormattedString();
        text.Spans.Add(new Span
        {
            Text = $"{DeviceDatasetGroup.DisplayName(group.Family)}  ",
            TextColor = foreground,
            FontFamily = "QuicksandSemiBold",
            FontSize = 11,
        });
        text.Spans.Add(new Span
        {
            Text = group.Detail,
            TextColor = (Color)Microsoft.Maui.Controls.Application.Current!.Resources["BodyText"],
            FontFamily = "Quicksand",
            FontSize = 11,
        });

        return new Label { FormattedText = text };
    }

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

    /// <summary>
    /// Restores the disclosure state after a reload. The page re-creates every card on refresh,
    /// so without this an open detail would snap shut each time an action reloads the list.
    /// </summary>
    public void SetSharingExpanded(bool expanded)
    {
        SharingDetail.IsVisible = _canExpandSharing && expanded;
        SharingChevron.Source = SharingDetail.IsVisible ? "icon_chevron.svg" : "icon_chevron_down.svg";
        SemanticProperties.SetDescription(SharingHeader, !_canExpandSharing
            ? "Shared data"
            : SharingDetail.IsVisible ? "Shared data, expanded" : "Shared data, collapsed");
        SemanticProperties.SetHint(SharingHeader, !_canExpandSharing
            ? string.Empty
            : SharingDetail.IsVisible ? "Hides each reading" : "Lists each reading shared");
    }

    private void SetSharingExpandable(bool expandable)
    {
        _canExpandSharing = expandable;
        SharingChevron.IsVisible = expandable;
        // Re-applied either way so the chevron glyph and the semantic description match the
        // panel after a rebuild, whichever state the card was left in.
        SetSharingExpanded(SharingDetail.IsVisible);
    }

    private void OnSharingTapped(object? sender, TappedEventArgs e)
    {
        if (!_canExpandSharing)
            return;

        var expanded = !SharingDetail.IsVisible;
        SetSharingExpanded(expanded);
        SharingExpansionChanged?.Invoke(this, expanded);
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

    private void OnPrimaryToggled(object? sender, ToggledEventArgs e)
    {
        if (_applying || !e.Value)
            return;
        SetPrimaryRequested?.Invoke(this, _deviceId);
    }
}
