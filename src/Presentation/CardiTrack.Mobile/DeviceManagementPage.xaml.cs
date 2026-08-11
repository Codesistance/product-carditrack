using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Controls;
using CardiTrack.Mobile.Core.Api;
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
    private bool _isLoading;
    private bool _isBusy;
    private bool _wizardActive;
    private CardiMemberDetailResponse? _member;

    public DeviceManagementPage(ICardiTrackApiClient api, IPopupService popups)
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

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = LoadAsync();
    }

    private async void OnPullToRefresh(object? sender, EventArgs e)
    {
        await LoadAsync();
        Refresher.IsRefreshing = false;
    }

    private void OnRetryClicked(object? sender, EventArgs e) => _ = LoadAsync();

    private async Task LoadAsync()
    {
        if (_isLoading)
            return;
        _isLoading = true;

        if (_cards.Count == 0)
            SetState(loading: true);

        try
        {
            // The member is fetched alongside the devices so the list can be headed by whose
            // devices these are — M1-15 groups by CardiMember. WhenAll rather than awaiting in
            // turn: if the first call fails, awaiting it alone would leave the second task's
            // exception unobserved.
            var memberTask = _api.GetCardiMemberAsync(_memberId);
            var devicesTask = _api.GetDevicesAsync(_memberId);
            await Task.WhenAll(memberTask, devicesTask);
            _member = await memberTask;
            var devices = await devicesTask;

            MemberSubtitleLabel.Text = $"{_member.Name} • CardiMember";
            EmptyDetailLabel.Text =
                $"Connect a wearable so CardiTrack can start watching over {NameFormatting.FirstName(_member.Name)}.";
            Render(devices.Devices);
            SetState(loaded: true);
        }
        catch (ApiException ex)
        {
            if (_cards.Count == 0)
            {
                ErrorDetailLabel.Text = ex.Message;
                SetState(error: true);
            }
            else if (!ex.IsSessionExpired)
            {
                // An expired session is already taking the user back to sign-in — a popup
                // here would only land on top of that page explaining nothing.
                await _popups.ShowWarningAsync(ex.Message, "Couldn't refresh");
            }
        }
        finally
        {
            _isLoading = false;
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

    // Named rather than "..": this page is also reached via the Notifications inbox, which
    // pushes it directly with no member detail page underneath it on the stack.
    private async void OnBackClicked(object? sender, EventArgs e) =>
        await Shell.Current.GoToAsync($"{AppShell.DashboardRoute}/{CardiMemberDetailPage.Route}?memberId={_memberId}");

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

            await LoadAsync();
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
            await LoadAsync();
        }
        catch (ApiException ex) when (!ex.IsSessionExpired)
        {
            await _popups.ShowErrorAsync(ex.Message, errorTitle);
            // Re-read state so a half-applied toggle doesn't linger on screen.
            await LoadAsync();
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
