using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Export;
using CardiTrack.Mobile.Services;
using MauiApplication = Microsoft.Maui.Controls.Application;

namespace CardiTrack.Mobile;

public partial class ExportConsentsPage : ContentPage
{
    public const string Route = "exportconsents";

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;
    private bool _loading;

    public ExportConsentsPage(ICardiTrackApiClient api, IPopupService popups)
    {
        InitializeComponent();
        _api = api;
        _popups = popups;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_popups.IsShowing)
            return;
        _ = LoadAsync();
    }

    private async void OnBackTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToAsync("..");

    private async void OnRetryClicked(object? sender, EventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        if (_loading)
            return;
        _loading = true;
        Loading.IsVisible = true;
        Panel.IsVisible = false;
        ErrorPanel.IsVisible = false;

        try
        {
            Render(await _api.GetExportConsentsAsync());
            Panel.IsVisible = true;
        }
        catch (ApiException)
        {
            ErrorPanel.IsVisible = true;
        }
        finally
        {
            Loading.IsVisible = false;
            _loading = false;
        }
    }

    private void Render(IReadOnlyList<ExportConsentHistoryItem> items)
    {
        ActiveList.Clear();
        HistoryList.Clear();

        var active = items.Where(i => i.CanRevoke).ToList();
        var earlier = items.Where(i => !i.CanRevoke).ToList();

        foreach (var item in active)
            ActiveList.Add(BuildActiveRow(item));

        foreach (var item in earlier)
            HistoryList.Add(BuildHistoryRow(item));

        HistoryTitle.IsVisible = earlier.Count > 0;
        EmptyLabel.IsVisible = items.Count == 0;
    }

    private View BuildActiveRow(ExportConsentHistoryItem item)
    {
        var stop = new Button
        {
            Text = "Stop",
            Style = ButtonStyle("SecondaryOutlineButton"),
            HeightRequest = 42,
            HorizontalOptions = LayoutOptions.Fill,
            Margin = new Thickness(0, 4, 0, 0)
        };
        stop.Clicked += async (_, _) => await RevokeAsync(item);

        var stack = new VerticalStackLayout { Spacing = 6 };
        stack.Add(TitleLabel("In force"));
        stack.Add(BodyLabel(ExportConsentCopy.HistorySummary(item)));
        stack.Add(CaptionLabel($"Given {item.RecordedAt.ToLocalTime():d MMM yyyy}"));
        stack.Add(stop);
        return Card(stack);
    }

    private static View BuildHistoryRow(ExportConsentHistoryItem item)
    {
        var stack = new VerticalStackLayout { Spacing = 4 };
        stack.Add(TitleLabel(item.RecordedAt.ToLocalTime().ToString("d MMM yyyy")));
        stack.Add(BodyLabel(ExportConsentCopy.HistorySummary(item)));
        return Card(stack);
    }

    private async Task RevokeAsync(ExportConsentHistoryItem item)
    {
        var confirmed = await _popups.ConfirmWarningAsync(
            "The next export will ask you to confirm again. Earlier copies you already made are unchanged.",
            "Stop this confirmation?",
            "Stop it",
            "Keep it");
        if (!confirmed)
            return;

        try
        {
            await _api.RevokeExportConsentAsync(item.Id);
            await LoadAsync();
        }
        catch (ApiException ex)
        {
            await _popups.ShowErrorAsync(ex.Message, "Couldn't stop that");
        }
    }

    private static Border Card(View content)
    {
        var border = new Border { Padding = new Thickness(16, 14) };
        if (MauiApplication.Current?.Resources.TryGetValue("ElevatedCard", out var style) == true
            && style is Style card)
        {
            border.Style = card;
        }

        border.Content = content;
        return border;
    }

    private static Label TitleLabel(string text)
    {
        var label = new Label { Text = text };
        ApplyStyle(label, "Body1SemiBoldDark");
        return label;
    }

    private static Label BodyLabel(string text)
    {
        var label = new Label { Text = text, LineBreakMode = LineBreakMode.WordWrap };
        ApplyStyle(label, "Body2");
        return label;
    }

    private static Label CaptionLabel(string text)
    {
        var label = new Label { Text = text };
        ApplyStyle(label, "Body2");
        return label;
    }

    private static Style? ButtonStyle(string key) =>
        MauiApplication.Current?.Resources.TryGetValue(key, out var style) == true
            ? style as Style
            : null;

    private static void ApplyStyle(Label label, string key)
    {
        if (MauiApplication.Current?.Resources.TryGetValue(key, out var style) == true
            && style is Style s)
        {
            label.Style = s;
        }
    }
}
