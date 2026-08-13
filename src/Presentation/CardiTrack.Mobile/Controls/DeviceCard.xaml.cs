using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Devices;
using CardiTrack.Mobile.Services;
using Microsoft.Maui.Controls.Shapes;

namespace CardiTrack.Mobile.Controls;

/// <summary>One connected wearable on M1-15, with its refresh / primary / remove actions.</summary>
public partial class DeviceCard : ContentView
{
    public event EventHandler<Guid>? RefreshRequested;
    public event EventHandler<Guid>? SetPrimaryRequested;
    public event EventHandler<Guid>? RemoveRequested;

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

            SyncedLabel.Text = device.LastSyncedAt is { } synced
                ? $"synced {RelativeTime.Format(synced)}"
                : "not synced yet";

            ApplyDatasets(device.Scopes);

            LastSyncValue.Text = device.LastSyncedAt is { } last
                ? RelativeTime.Format(last)
                : "—";
            NextSyncValue.Text = NextSyncText(device.NextSyncAt);
            TodayValue.Text = device.TodayUpdateCount switch
            {
                0 => "No updates",
                1 => "1 update",
                var n => $"{n} updates",
            };

            PrimaryStar.IsVisible = device.IsPrimary;
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

    /// <summary>Disables the actions while a request for this card is in flight.</summary>
    public void SetBusy(bool busy)
    {
        IsEnabled = !busy;
        RefreshLabel.Text = busy ? "Working..." : "Refresh Connection";
    }

    private static string NextSyncText(DateTime? nextSyncAt)
    {
        if (nextSyncAt is not { } next)
            return "When connected";

        var minutes = (int)Math.Ceiling(
            (DateTime.SpecifyKind(next, DateTimeKind.Utc) - DateTime.UtcNow).TotalMinutes);
        return minutes switch
        {
            <= 0 => "Any moment",
            1 => "In 1 min",
            < 60 => $"In {minutes} mins",
            _ => $"In {minutes / 60}h",
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

        var pill = Pill(background, new Label { FormattedText = text });

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

    private static Border Pill(Color background, Label content) => new()
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

    private void OnRefreshTapped(object? sender, TappedEventArgs e) =>
        RefreshRequested?.Invoke(this, _deviceId);

    private void OnRemoveTapped(object? sender, TappedEventArgs e) =>
        RemoveRequested?.Invoke(this, _deviceId);

    private void OnPrimaryToggled(object? sender, ToggledEventArgs e)
    {
        if (_applying || !e.Value)
            return;
        SetPrimaryRequested?.Invoke(this, _deviceId);
    }
}
