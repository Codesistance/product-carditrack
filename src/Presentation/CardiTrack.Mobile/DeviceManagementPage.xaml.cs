using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Controls;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Devices;
using CardiTrack.Mobile.Core.Offline;
using CardiTrack.Mobile.Onboarding;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

/// <summary>M1-15 Device Management. Entered from the M1-13 detail screen.</summary>
[QueryProperty(nameof(MemberId), "memberId")]
public partial class DeviceManagementPage : ContentPage
{
    public const string Route = "devicemanagement";

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;

    private readonly Dictionary<Guid, DeviceCard> _cards = [];
    private readonly Dictionary<Guid, string> _deviceNames = [];

    /// <summary>
    /// Devices whose sharing detail the user opened. Cards are rebuilt on every load, so the
    /// disclosure state has to live on the page or a pull-to-refresh would close it.
    /// </summary>
    private readonly HashSet<Guid> _expandedSharing = [];

    private Guid _memberId;
    private bool _isBusy;
    private bool _wizardActive;
    private DateTime _lastLoadedUtc = DateTime.MinValue;
    private CardiMemberDetailResponse? _member;

    private readonly LoadGate _gate = new();
    private readonly RefreshFeedback _feedback;

    /// <summary>What one load of this page reads: the member heads the list of their devices.</summary>
    private sealed record DeviceLoad(CardiMemberDetailResponse Member, DeviceListResponse Devices);

    /// <summary>The last load put on screen, saved or live — null until something is.</summary>
    private DeviceLoad? _last;

    public DeviceManagementPage(ICardiTrackApiClient api, IPopupService popups)
    {
        InitializeComponent();
        _api = api;
        _popups = popups;
        _feedback = new RefreshFeedback(SavedBanner, Updating);
        this.RefreshWhenAppResumes(RefreshOnResumeAsync);
    }

    public string MemberId
    {
        set => _memberId = Guid.TryParse(Uri.UnescapeDataString(value ?? string.Empty), out var id)
            ? id
            : Guid.Empty;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = LoadAsync();
    }

    private async void OnPullToRefresh(object? sender, EventArgs e)
    {
        await LoadAsync(force: true);
        Refresher.IsRefreshing = false;
    }

    private void OnRetryClicked(object? sender, EventArgs e) => _ = LoadAsync(force: true);

    /// <summary>
    /// The app returning to the foreground reloads the device list — a connection that dropped
    /// or a sync that landed while the app was away is exactly what this screen is consulted
    /// about. Silent: an unrequested refresh that fails leaves the list as it was.
    /// </summary>
    private Task RefreshOnResumeAsync() =>
        DateTime.UtcNow - _lastLoadedUtc < ResumeRefresh.MinimumGap
            ? Task.CompletedTask
            : LoadAsync(silent: true);

    /// <param name="silent">
    /// Suppresses the "Couldn't refresh" popup for loads the user did not ask for.
    /// </param>
    /// <param name="force">
    /// Supersedes a load already in flight rather than skipping — a pull, a retry, and the
    /// re-read after a device action, which replaces rows whose ids the server may have just
    /// changed. Only the unattended resume waits its turn.
    /// </param>
    private async Task LoadAsync(bool silent = false, bool force = false)
    {
        if (_gate.IsLoading && !force)
            return;
        var ticket = _gate.Begin();
        var memberId = _memberId;

        if (_last is null)
            SetState(loading: true);

        try
        {
            // The member is fetched alongside the devices so the list can be headed by whose
            // devices these are — M1-15 groups by CardiMember. The device's saved pair goes up
            // first on a landing with nothing on screen, and only when both halves are saved:
            // half a page is not a page. WhenAll rather than awaiting in turn on the live side:
            // if the first call fails, awaiting it alone would leave the second task's exception
            // unobserved.
            var outcome = await SnapshotRefresh.RunAsync<DeviceLoad>(
                _api, _gate, ticket,
                peek: _last is null
                    ? async (ct, scope) =>
                    {
                        var member = await scope.Track(_api.PeekCardiMemberAsync(memberId, ct));
                        var devices = await scope.Track(_api.PeekDevicesAsync(memberId, ct));
                        return member is null || devices is null ? null : new DeviceLoad(member, devices);
                    }
                    : null,
                fetch: async (ct, scope) =>
                {
                    var memberTask = scope.Track(_api.GetCardiMemberAsync(memberId, ct));
                    var devicesTask = scope.Track(_api.GetDevicesAsync(memberId, ct));
                    await Task.WhenAll(memberTask, devicesTask);
                    return new DeviceLoad(await memberTask, await devicesTask);
                },
                render: load =>
                {
                    _last = load;
                    _member = load.Member;
                    ChatBot.MemberId = memberId;
                    ChatBot.MemberFirstName = NameFormatting.FirstName(load.Member.Name);
                    MemberSubtitleLabel.Text = $"{load.Member.Name} • CardiMember";
                    EmptyDetailLabel.Text =
                        $"Connect a wearable so CardiTrack can start watching over {NameFormatting.FirstName(load.Member.Name)}.";
                    Render(load.Devices.Devices);
                    SetState(loaded: true);
                },
                _feedback);

            switch (outcome.Result)
            {
                case RefreshResult.Superseded:
                    return;
                case RefreshResult.NothingAndFailed:
                    _last = null;
                    _member = null;
                    ErrorDetailLabel.Text = outcome.Error!.Message;
                    SetState(error: true);
                    return;
            }

            if (outcome.IsFresh)
                _lastLoadedUtc = DateTime.UtcNow;
            else if (!silent && outcome.Error is { IsSessionExpired: false } error)
            {
                // An expired session is already taking the user back to sign-in — a popup
                // here would only land on top of that page explaining nothing.
                await _popups.ShowWarningAsync(error.Message, "Couldn't refresh");
            }
        }
        finally
        {
            _gate.Release(ticket);
        }
    }

    private void Render(List<DeviceResponse> devices)
    {
        DevicesStack.Clear();
        _cards.Clear();
        _deviceNames.Clear();

        EmptyPanel.IsVisible = devices.Count == 0;
        DevicesStack.IsVisible = devices.Count > 0;

        // A device that has gone would otherwise keep its entry here for the life of the page.
        _expandedSharing.IntersectWith(devices.Select(d => d.DeviceId));

        foreach (var device in devices)
        {
            var id = device.DeviceId;
            var card = new DeviceCard();
            card.Apply(device);
            card.SetSharingExpanded(_expandedSharing.Contains(id));
            card.RefreshRequested += OnRefreshRequested;
            card.RepullRequested += OnRepullRequested;
            card.SetPrimaryRequested += OnSetPrimaryRequested;
            card.RemoveRequested += OnRemoveRequested;
            card.SharingExpansionChanged += (_, expanded) =>
            {
                if (expanded)
                    _expandedSharing.Add(id);
                else
                    _expandedSharing.Remove(id);
            };
            _cards[id] = card;
            _deviceNames[id] = device.DisplayName;
            DevicesStack.Add(card);
        }
    }

    private void SetState(bool loading = false, bool loaded = false, bool error = false)
    {
        SkeletonPanel.IsVisible = loading;
        ContentPanel.IsVisible = loaded;
        ErrorPanel.IsVisible = error;
    }

    private void OnToggleHelpTapped(object? sender, TappedEventArgs e)
    {
        HelpPanel.IsVisible = !HelpPanel.IsVisible;
        HelpChevron.Source = HelpPanel.IsVisible ? "icon_chevron.svg" : "icon_chevron_down.svg";
    }

    // History first, so arriving from the Notifications inbox returns to the inbox rather than to
    // a Member Detail page the caregiver was never on. Member Detail stays the floor: it is where
    // this screen belongs when it was opened with nothing behind it.
    private async void OnBackClicked(object? sender, EventArgs e) =>
        await this.GoBackAsync($"{AppShell.DashboardRoute}/{CardiMemberDetailPage.Route}?memberId={_memberId}");

    private async void OnAddDeviceClicked(object? sender, EventArgs e)
    {
        if (_wizardActive || _member is null)
            return;
        _wizardActive = true;
        try
        {
            // The connect wizard wants the list shape, not the detail shape.
            var members = await _api.GetCardiMembersAsync();
            var member = members.FirstOrDefault(m => m.Id == _memberId);
            if (member is null)
                return;

            var result = await WizardLauncher.RunModalAsync(
                Navigation, member, showBaselineIntro: _cards.Count == 0);

            // "Go to Dashboard" has taken the shell off this page — reloading it here would
            // fetch for a page that is gone, and surface its errors over the dashboard.
            if (result.ExitedToDashboard)
                return;

            await LoadAsync(force: true);
        }
        catch (ApiException ex) when (!ex.IsSessionExpired)
        {
            await _popups.ShowErrorAsync(ex.Message, "Couldn't start device setup");
        }
        catch (ApiException)
        {
            // Session gone — the app is already on its way back to sign-in.
        }
        catch (Exception ex)
        {
            // async void — a failed modal push must not take the app down.
            await _popups.ShowErrorAsync(ex.Message, "Couldn't start device setup");
        }
        finally
        {
            _wizardActive = false;
        }
    }

    private async void OnRefreshRequested(object? sender, Guid deviceId) =>
        await RunDeviceActionAsync(deviceId, async () =>
        {
            await _api.RefreshDeviceConnectionAsync(_memberId, deviceId);
        }, "Couldn't refresh this connection");

    /// <summary>
    /// Re-pull History: pick how far back, confirm, queue. Info-styled confirmation rather than
    /// a warning — nothing is destroyed, a re-pull only fills and refreshes days — but still a
    /// confirmation, because it spends the wearer's provider quota and blocks another for two
    /// days. The reload afterwards is what shows "Queued" on the card.
    /// </summary>
    private async void OnRepullRequested(object? sender, Guid deviceId)
    {
        if (_isBusy)
            return;

        var name = _deviceNames.TryGetValue(deviceId, out var displayName) ? displayName : "this device";
        var choice = await _popups.ChooseAsync(
            $"How far back for {name}?", "Cancel", HistoryRepullCopy.ChoiceLabels());
        if (HistoryRepullCopy.DaysFor(choice) is not { } days)
            return;

        var confirmed = await _popups.ConfirmInfoAsync(
            $"We'll re-read the last {days} days from {name}'s provider in the background. " +
            $"It takes {HistoryRepullCopy.Estimate(days)}, only fills days that have data, and never removes anything.",
            "Re-pull history?",
            "Yes, re-pull");
        if (!confirmed)
            return;

        await RunDeviceActionAsync(deviceId, async () =>
        {
            await _api.RequestHistoryRepullAsync(_memberId, deviceId, days);
        }, "Couldn't start the re-pull");
    }

    private async void OnSetPrimaryRequested(object? sender, Guid deviceId) =>
        await RunDeviceActionAsync(deviceId, async () =>
        {
            await _api.SetPrimaryDeviceAsync(_memberId, deviceId);
        }, "Couldn't set the primary device");

    private async void OnRemoveRequested(object? sender, Guid deviceId)
    {
        if (_isBusy)
            return;

        var name = _deviceNames.TryGetValue(deviceId, out var displayName) ? displayName : "this device";
        var confirmed = await _popups.ConfirmWarningAsync(
            $"We'll stop collecting data from {name} and forget its connection. " +
            "You can connect it again at any time.",
            $"Remove {name}?",
            "Yes, remove");
        if (!confirmed)
            return;

        await RunDeviceActionAsync(deviceId, async () =>
        {
            await _api.DisconnectDeviceAsync(_memberId, deviceId);
        }, "Couldn't remove this device");
    }

    /// <summary>
    /// Runs a per-device call, then reloads. Reloading rather than patching the card in place
    /// keeps the list honest: setting one device primary demotes another, and removing one can
    /// promote a replacement — both are server-side decisions.
    /// </summary>
    private async Task RunDeviceActionAsync(Guid deviceId, Func<Task> action, string errorTitle)
    {
        if (_isBusy)
            return;
        _isBusy = true;
        _cards.TryGetValue(deviceId, out var card);
        card?.SetBusy(true);

        try
        {
            await action();
            await LoadAsync(force: true);
        }
        catch (ApiException ex) when (!ex.IsSessionExpired)
        {
            await _popups.ShowErrorAsync(ex.Message, errorTitle);
            // Re-read state so a half-applied toggle doesn't linger on screen.
            await LoadAsync(force: true);
        }
        catch (ApiException)
        {
            // Session gone — the app is already on its way back to sign-in.
        }
        finally
        {
            card?.SetBusy(false);
            _isBusy = false;
        }
    }
}
