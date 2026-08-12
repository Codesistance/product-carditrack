using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Controls;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Notifications;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

/// <summary>
/// The data-completeness inbox: what CardiTrack is missing, what supplying it unlocks, and the
/// three things a caregiver can do about each — fix it, put it off, or turn it off.
/// </summary>
public partial class NotificationsPage : ContentPage
{
    public const string Route = "notifications";

    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromMinutes(2);

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;

    private enum PageState { Loading, Loaded, Empty, Error }

    private bool _isLoading;
    private CancellationTokenSource? _loadCts;
    private DateTime _lastLoadedUtc = DateTime.MinValue;
    private NotificationListResponse? _lastData;

    public NotificationsPage(ICardiTrackApiClient api, IPopupService popups)
    {
        InitializeComponent();
        _api = api;
        _popups = popups;
        this.RefreshWhenAppResumes(RefreshOnResumeAsync);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_lastData is null || DateTime.UtcNow - _lastLoadedUtc > AutoRefreshInterval)
            _ = LoadAsync();
    }

    /// <summary>
    /// The app returning to the foreground reloads the inbox, ignoring
    /// <see cref="AutoRefreshInterval"/> — items resolved elsewhere (or raised by the workers)
    /// while the app was away should be settled by the time the caregiver looks at the list.
    /// </summary>
    private Task RefreshOnResumeAsync() =>
        DateTime.UtcNow - _lastLoadedUtc < ResumeRefresh.MinimumGap
            ? Task.CompletedTask
            : LoadAsync();

    private async Task LoadAsync(bool force = false)
    {
        if (_isLoading && !force)
            return;

        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        _isLoading = true;

        if (_lastData is null)
            SetState(PageState.Loading);

        try
        {
            var data = await _api.GetNotificationsAsync(
                state: nameof(NotificationState.Open), ct: cts.Token);

            if (cts.IsCancellationRequested)
                return;

            _lastData = data;
            _lastLoadedUtc = DateTime.UtcNow;
            Render(data);
        }
        catch (ApiException ex)
        {
            // A superseded request surfaces its cancellation as a transport failure; that is this
            // page's own doing and must not be shown to the user as "no connection".
            if (cts.IsCancellationRequested)
                return;

            if (_lastData is null)
            {
                ErrorDetailLabel.Text = ex.Message;
                SetState(PageState.Error);
            }
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                _isLoading = false;
                Refresher.IsRefreshing = false;
            }
        }
    }

    private void Render(NotificationListResponse data)
    {
        OwnedList.Clear();
        FamilyList.Clear();

        var owned = data.Items.Where(n => n.IsOwner).ToList();
        var family = data.Items.Where(n => !n.IsOwner).ToList();

        foreach (var notification in owned)
            OwnedList.Add(BuildCard(notification));

        foreach (var notification in family)
            FamilyList.Add(BuildCard(notification));

        FamilySection.IsVisible = family.Count > 0;
        SubtitleLabel.Text = owned.Count switch
        {
            0 => "You're all set",
            1 => "1 thing to look at",
            _ => $"{owned.Count} things to look at"
        };

        SetState(data.Items.Count == 0 ? PageState.Empty : PageState.Loaded);

        // Marking seen is what gives the comply funnel a denominator, and it is deliberately
        // fire-and-forget: a caregiver has read the card whether or not the write lands.
        foreach (var notification in owned.Where(n => n.FirstSeenDate is null))
            _ = MarkSeenQuietlyAsync(notification.Id);
    }

    private NudgeCard BuildCard(NotificationResponse notification)
    {
        var card = new NudgeCard();
        card.Bind(notification);
        card.ComplyRequested += OnComplyRequested;
        card.SnoozeRequested += OnSnoozeRequested;
        card.DismissRequested += OnDismissRequested;
        return card;
    }

    private async Task MarkSeenQuietlyAsync(Guid notificationId)
    {
        try
        {
            await _api.MarkNotificationSeenAsync(notificationId);
        }
        catch (ApiException)
        {
            // Funnel bookkeeping is never worth interrupting someone for.
        }
    }

    // ------------------------------------------------------------------ actions

    private async void OnComplyRequested(object? sender, NotificationResponse notification)
    {
        // The phone already knows its own zone, so this one is answered in a tap rather than by
        // sending someone into a list of four hundred IANA identifiers to find their own city.
        if (NudgeLinkParser.Parse(notification.ActionDeepLink).Kind == NudgeDestinationKind.TimeZone)
        {
            await ConfirmTimeZoneAsync();
            return;
        }

        var destination = DeepLinkRouter.Resolve(notification.ActionDeepLink);

        if (destination is null)
        {
            // Better to say so than to swallow the tap: a button that does nothing teaches the
            // user the whole surface is decorative.
            await _popups.ShowInfoAsync(
                "That screen isn't built in this version of the app.",
                "Not available yet");
            return;
        }

        await Shell.Current.GoToAsync(destination);
    }

    private async Task ConfirmTimeZoneAsync()
    {
        var local = TimeZoneInfo.Local;

        // "UTC" as the device zone means the phone itself is unset — offering to copy it would
        // just write the default back and leave the nudge standing.
        if (string.Equals(local.Id, "UTC", StringComparison.OrdinalIgnoreCase))
        {
            await _popups.ShowInfoAsync(
                "This phone is also set to UTC. Set its time zone in system settings, then come back.",
                "Set your time zone");
            return;
        }

        var confirmed = await _popups.ConfirmWarningAsync(
            $"We'll set your account to {local.Id}.",
            "Use this phone's time zone?",
            confirmText: "Use it");

        if (!confirmed)
            return;

        await MutateAsync(() => _api.UpdateTimeZoneAsync(local.Id));
    }

    private async void OnSnoozeRequested(object? sender, NotificationResponse notification)
    {
        var maxHours = notification.MaxSnoozeHours;

        // Options are filtered by the rule's own ceiling rather than offered and then clamped
        // server-side — a safety rule showing "A month" it cannot honour would be a lie.
        var options = new (string Label, TimeSpan Duration)[]
        {
            ("Tomorrow", TimeSpan.FromDays(1)),
            ("Next week", TimeSpan.FromDays(7)),
            ("In a month", TimeSpan.FromDays(30))
        }
        .Where(o => o.Duration.TotalHours <= maxHours)
        .ToArray();

        if (options.Length == 0)
            options = [("In 3 days", TimeSpan.FromHours(Math.Min(72, maxHours)))];

        var choice = await _popups.ChooseAsync(
            "Remind me…", "Cancel", [.. options.Select(o => o.Label)]);

        var chosen = options.FirstOrDefault(o => o.Label == choice);
        if (chosen.Label is null)
            return;

        await MutateAsync(() => _api.SnoozeNotificationAsync(notification.Id, chosen.Duration));
    }

    private async void OnDismissRequested(object? sender, NotificationResponse notification)
    {
        // Safety rules never reach here — NudgeCard hides the affordance — but the confirmation
        // is written for them anyway, so a future safety rule cannot be silenced by accident if
        // someone re-enables the button.
        // "Reminders", never "alerts" — alerts are the health stream, and a caregiver who thinks
        // they have just silenced those has been told something untrue about their relative's care.
        var confirmed = await _popups.ConfirmWarningAsync(
            notification.CanMute
                ? "You won't be reminded about this again. You can bring it back from Settings."
                : "We'll stop reminding you, but the gap stays open until you fix it. "
                  + "You can bring the reminder back from Settings.",
            "Stop reminding me?",
            confirmText: "Stop reminding me",
            cancelText: "Keep it");

        if (!confirmed)
            return;

        await MutateAsync(() => _api.DismissNotificationAsync(
            notification.Id, acknowledgedConsequence: !notification.CanMute));
    }

    /// <summary>
    /// Runs an action and reloads. Reloading rather than mutating the list in place keeps the
    /// screen a projection of server state, which is the same property that lets a gap the user
    /// fixed elsewhere disappear from here without anyone dismissing it.
    /// </summary>
    private async Task MutateAsync(Func<Task> action)
    {
        try
        {
            await action();
            await LoadAsync(force: true);
        }
        catch (ApiException ex)
        {
            await _popups.ShowErrorAsync(ex.Message, "That didn't work");
        }
    }

    // ------------------------------------------------------------------ chrome

    private void SetState(PageState state)
    {
        SkeletonPanel.IsVisible = state == PageState.Loading;
        ContentPanel.IsVisible = state == PageState.Loaded;
        EmptyPanel.IsVisible = state == PageState.Empty;
        ErrorPanel.IsVisible = state == PageState.Error;

        if (state == PageState.Empty)
            SubtitleLabel.Text = "You're all set";
    }

    private void OnPullToRefresh(object? sender, EventArgs e) => _ = LoadAsync(force: true);

    private void OnRetryClicked(object? sender, EventArgs e) => _ = LoadAsync(force: true);

    // ".." alone went nowhere when this page was opened with nothing behind it — a notification
    // tap, or a deep link straight into the inbox. The dashboard is the floor for those.
    private async void OnBackClicked(object? sender, EventArgs e) =>
        await this.GoBackAsync(AppShell.DashboardRoute);
}
