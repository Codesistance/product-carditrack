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

    private Guid _deviceId;

    /// <summary>Guards the Toggled handler while <see cref="Apply"/> sets the switch itself.</summary>
    private bool _applying;

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
    /// Rebuilds the dataset pill row. A connection sharing nothing is worth saying out loud —
    /// it looks connected but sends no data — so the row keeps a pill either way.
    /// </summary>
    private void ApplyDatasets(List<string> scopes)
    {
        DatasetPills.Children.Clear();

        var datasets = DeviceDatasets.For(scopes);
        if (datasets.Count == 0)
        {
            DatasetPills.Children.Add(BuildPill("No data shared", DatasetFamily.Other));
            return;
        }

        foreach (var dataset in datasets)
            DatasetPills.Children.Add(BuildPill(dataset.Name, dataset.Family));
    }

    private static Border BuildPill(string text, DatasetFamily family)
    {
        var (background, foreground) = PillColours(family);

        return new Border
        {
            StrokeThickness = 0,
            BackgroundColor = background,
            Padding = new Thickness(10, 4),
            // FlexLayout has no spacing of its own; the margin is the gutter between pills.
            Margin = new Thickness(0, 0, 6, 6),
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            Content = new Label
            {
                Text = text,
                TextColor = foreground,
                FontFamily = "QuicksandSemiBold",
                FontSize = 11,
            },
        };
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

    private static string ProviderImageFor(string provider) => provider.ToLowerInvariant() switch
    {
        "fitbit" => "device_fitbit.png",
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
