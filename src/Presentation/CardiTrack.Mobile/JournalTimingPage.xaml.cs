using System.Globalization;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

/// <summary>
/// When this CardiMember's CardiJournal books are written, in their own local time.
/// </summary>
/// <remarks>
/// <para>
/// Every row saves the moment it is picked. There is no Save button because there is no
/// half-made state worth holding: one row is one value, and committing it immediately is what
/// the alert-rule toggles on the member detail page already do.
/// </para>
/// <para>
/// The picker offers only times the server said it would accept — the window and the half-hour
/// step come down on the response rather than being hard-coded here, so the app cannot offer a
/// time the generator would refuse and leave the caregiver reading an error they did not cause.
/// </para>
/// </remarks>
[QueryProperty(nameof(MemberId), "memberId")]
[QueryProperty(nameof(MemberName), "name")]
public partial class JournalTimingPage : ContentPage
{
    public const string Route = "journaltiming";

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;

    private Guid _memberId;
    private JournalSettingsResponse? _settings;
    private bool _saving;

    public JournalTimingPage(ICardiTrackApiClient api, IPopupService popups)
    {
        InitializeComponent();
        _api = api;
        _popups = popups;
    }

    public string MemberId
    {
        set => _memberId = Guid.TryParse(Uri.UnescapeDataString(value ?? string.Empty), out var id)
            ? id
            : Guid.Empty;
    }

    public string MemberName
    {
        set
        {
            var name = Uri.UnescapeDataString(value ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(name))
                HeaderSubtitle.Text = $"{name}'s CardiJournal";
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // A popup closing raises this again; reloading under a caregiver mid-choice would
        // replace the values they are reading with the same ones fetched twice.
        if (_popups.IsShowing || _settings is not null)
            return;

        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _settings = await _api.GetJournalSettingsAsync(_memberId);
            Render(_settings);
        }
        catch (ApiException ex)
        {
            ErrorDetailLabel.Text = ex.Message;
            LoadingSpinner.IsVisible = false;
            LoadingSpinner.IsRunning = false;
            ErrorPanel.IsVisible = true;
        }
    }

    private void Render(JournalSettingsResponse settings)
    {
        LoadingSpinner.IsVisible = false;
        LoadingSpinner.IsRunning = false;
        ErrorPanel.IsVisible = false;
        SettingsPanel.IsVisible = true;

        DaybookValue.Text = Format(settings.EffectiveDaybookLocalTime);
        DaybookSubtitle.Text = "Each finished day, written up";

        WeekStartValue.Text = settings.EffectiveWeekStartsOn.ToString();
        WeekStartSubtitle.Text = "A Weekbook covers the seven days ending the night before";

        WeekbookValue.Text = Format(settings.EffectiveWeekbookLocalTime);
        WeekbookSubtitle.Text = settings.WeekbookAvailable
            ? $"Each {settings.EffectiveWeekStartsOn}, for the week just gone"
            : "Saved — starts writing when weekly summaries arrive";

        MonthbookValue.Text = Format(settings.EffectiveMonthbookLocalTime);
        MonthbookSubtitle.Text = settings.MonthbookAvailable
            ? "On the 1st, for the month just gone"
            : "Saved — starts writing when monthly summaries arrive";

        // Their clock, not the caregiver's: someone in London setting 07:00 for a parent in
        // Lagos is choosing 07:00 in Lagos, and the footnote is where that stops being a
        // surprise. Not midnight, because the last hours of a day arrive after it and a book
        // is never rewritten.
        FootnoteLabel.Text =
            $"Times are {(string.IsNullOrWhiteSpace(settings.TimeZoneId) ? "in their own local time" : $"in their own local time ({settings.TimeZoneId})")}, "
            + $"between {Format(settings.EarliestSelectableTime)} and {Format(settings.LatestSelectableTime)}. "
            + "A book is written once and never rewritten, so it waits for the last readings of the period to arrive.";
    }

    private static string Format(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    private async void OnDaybookTimeTapped(object? sender, TappedEventArgs e) =>
        await ChooseTimeAsync(
            "When to write the Daybook",
            _settings?.EffectiveDaybookLocalTime,
            picked => BuildRequest(_settings!) with { DaybookLocalTime = picked });

    private async void OnWeekbookTimeTapped(object? sender, TappedEventArgs e) =>
        await ChooseTimeAsync(
            "When to write the Weekbook",
            _settings?.EffectiveWeekbookLocalTime,
            picked => BuildRequest(_settings!) with { WeekbookLocalTime = picked });

    private async void OnMonthbookTimeTapped(object? sender, TappedEventArgs e) =>
        await ChooseTimeAsync(
            "When to write the Monthbook",
            _settings?.EffectiveMonthbookLocalTime,
            picked => BuildRequest(_settings!) with { MonthbookLocalTime = picked });

    private async Task ChooseTimeAsync(
        string title, TimeOnly? current, Func<TimeOnly, JournalSettingsDraft> apply)
    {
        if (_settings is null || _saving)
            return;

        var options = SelectableTimes(_settings).Select(Format).ToArray();
        var choice = await _popups.ChooseAsync(title, "Cancel", options);
        if (choice is null)
            return;

        if (!TimeOnly.TryParseExact(choice, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var picked))
            return;

        if (current == picked)
            return;

        await SaveAsync(apply(picked));
    }

    private async void OnWeekStartTapped(object? sender, TappedEventArgs e)
    {
        if (_settings is null || _saving)
            return;

        var days = Enum.GetValues<DayOfWeek>();
        var choice = await _popups.ChooseAsync(
            "A journal week starts on", "Cancel", days.Select(d => d.ToString()).ToArray());
        if (choice is null || !Enum.TryParse<DayOfWeek>(choice, out var picked))
            return;

        if (_settings.EffectiveWeekStartsOn == picked)
            return;

        await SaveAsync(BuildRequest(_settings) with { WeekStartsOn = picked });
    }

    private async Task SaveAsync(JournalSettingsDraft draft)
    {
        _saving = true;
        try
        {
            _settings = await _api.UpdateJournalSettingsAsync(_memberId, draft.ToRequest());
            Render(_settings);
        }
        catch (ApiException ex)
        {
            // The stored values are still whatever the server has; re-render from the last
            // known-good response rather than leaving a row showing a time that did not save.
            if (_settings is not null)
                Render(_settings);
            await _popups.ShowErrorAsync(ex.Message);
        }
        finally
        {
            _saving = false;
        }
    }

    /// <summary>
    /// The PUT is a full replacement of all four values, so every save resends the three the
    /// caregiver did not touch. Read from the last response rather than from the labels — a
    /// label is formatted text, and parsing it back would be a second source of truth.
    /// </summary>
    private static JournalSettingsDraft BuildRequest(JournalSettingsResponse settings) =>
        new(settings.EffectiveDaybookLocalTime,
            settings.EffectiveWeekbookLocalTime,
            settings.EffectiveMonthbookLocalTime,
            settings.EffectiveWeekStartsOn);

    private static IEnumerable<TimeOnly> SelectableTimes(JournalSettingsResponse settings)
    {
        var step = settings.StepMinutes > 0 ? settings.StepMinutes : 30;
        for (var time = settings.EarliestSelectableTime;
             time <= settings.LatestSelectableTime;
             time = time.AddMinutes(step))
        {
            yield return time;

            // AddMinutes wraps past midnight; without this a window ending at 12:00 would run
            // forever on a malformed pair rather than stopping.
            if (time.AddMinutes(step) < time)
                yield break;
        }
    }

    private async void OnBackTapped(object? sender, TappedEventArgs e) =>
        await this.GoBackAsync(
            $"{AppShell.DashboardRoute}/{CardiMemberDetailPage.Route}?memberId={_memberId}");

    private readonly record struct JournalSettingsDraft(
        TimeOnly DaybookLocalTime,
        TimeOnly WeekbookLocalTime,
        TimeOnly MonthbookLocalTime,
        DayOfWeek WeekStartsOn)
    {
        public UpdateJournalSettingsRequest ToRequest() => new()
        {
            DaybookLocalTime = DaybookLocalTime,
            WeekbookLocalTime = WeekbookLocalTime,
            MonthbookLocalTime = MonthbookLocalTime,
            WeekStartsOn = WeekStartsOn,
        };
    }
}
