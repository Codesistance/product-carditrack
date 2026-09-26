using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Controls;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Forms;
using CardiTrack.Mobile.Core.Members;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

/// <summary>
/// The admin's approval of a join request, with the member picker D-10 asks for.
/// </summary>
/// <remarks>
/// <para>
/// HIPAA minimum-necessary is the reason the picker exists rather than an all-members default: a
/// family watching two parents where different siblings handle each is the case that breaks
/// all-or-nothing, and it is not unusual.
/// </para>
/// <para>
/// The requester's name and email come from the queue that opened this page rather than from a
/// second fetch. There is no endpoint for one join request, and re-reading the whole queue to
/// find it again would make a 403 on a request the admin has since lost — approved from another
/// device, say — into a blank screen rather than a message.
/// </para>
/// </remarks>
[QueryProperty(nameof(OrganizationId), "organizationId")]
[QueryProperty(nameof(RequestId), "requestId")]
[QueryProperty(nameof(RequesterName), "name")]
public partial class ApproveJoinRequestPage : ContentPage
{
    public const string Route = "approvejoin";

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;

    private readonly Dictionary<Guid, CheckField> _checks = [];
    private Guid _organizationId;
    private Guid _requestId;
    private string _name = string.Empty;
    private bool _busy;
    private bool _loaded;

    public ApproveJoinRequestPage(ICardiTrackApiClient api, IPopupService popups)
    {
        InitializeComponent();
        _api = api;
        _popups = popups;
    }

    public string OrganizationId
    {
        set => _organizationId = Guid.TryParse(Uri.UnescapeDataString(value ?? string.Empty), out var id)
            ? id
            : Guid.Empty;
    }

    public string RequestId
    {
        set => _requestId = Guid.TryParse(Uri.UnescapeDataString(value ?? string.Empty), out var id)
            ? id
            : Guid.Empty;
    }

    public string RequesterName
    {
        set => _name = Uri.UnescapeDataString(value ?? string.Empty);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_loaded)
            return;
        _loaded = true;

        // Both query properties have to have landed before the load can name anybody; the setters
        // fire one at a time.
        RouteArrival.WhenRouteHasLanded(this, () => _ = LoadAsync());
    }

    private void OnRetryClicked(object? sender, EventArgs e) => _ = LoadAsync();

    private async void OnBackTapped(object? sender, TappedEventArgs e) =>
        await this.GoBackAsync(AppShell.FamilyRoute);

    private async Task LoadAsync()
    {
        if (_organizationId == Guid.Empty || _requestId == Guid.Empty)
        {
            ErrorDetailLabel.Text = "We couldn't tell which request this is. Open it again from the Family tab.";
            SetState(error: true);
            return;
        }

        SetState(loading: true);
        try
        {
            // The people this family watches, and only this family's: the admin's grants can span
            // several families, and offering one family's member to a joiner of another would
            // send an id the approval endpoint refuses — a rejection the admin could do nothing
            // about, from a list this page drew. Each member says which family owns it.
            var members = (await _api.GetCardiMembersAsync())
                .Where(m => m.OrganizationId == _organizationId)
                .ToList();
            var queue = await _api.GetPendingJoinRequestsAsync(_organizationId);
            var request = queue.FirstOrDefault(r => r.RequestId == _requestId);

            Apply(members, request);
            SetState(loaded: true);
        }
        catch (ApiException ex)
        {
            ErrorDetailLabel.Text = ex.Message;
            SetState(error: true);
        }
    }

    private void Apply(IReadOnlyList<CardiMemberResponse> members, PendingJoinRequest? request)
    {
        var name = request?.Name ?? _name;
        var display = string.IsNullOrWhiteSpace(name) ? "Somebody" : name;
        var first = NameFormatting.FirstName(display);

        HeaderTitle.Text = $"Let {first} In?";
        NameLabel.Text = display;
        AskedLabel.Text = request is null
            ? "This request may have been answered already."
            : $"{request.Email}\nAsked {RelativeTime.Format(request.RequestedAt)}";
        Avatar.BoxWidth = 48;
        Avatar.Apply(display, null);

        PickerHintLabel.Text = members.Count == 0
            ? "You aren't watching anybody yet, so there is nothing to share. They can still join the family."
            : $"{first} will see only the people you tick, and nothing about the others.";
        AlertsDetailLabel.Text =
            $"{first} gets a notification when something happens to the people you ticked — including the ones "
            + "nobody else has answered.";

        _checks.Clear();
        MembersHost.Clear();
        foreach (var member in members)
        {
            var check = new CheckField
            {
                Text = $"{member.DisplayFirstName()} · {member.Age}",
                IsChecked = false,
            };
            _checks[member.Id] = check;
            MembersHost.Add(check);
        }

        // A request that is no longer in the queue cannot be approved; the page still shows what
        // it was so the admin knows what they are looking at.
        ApproveButton.IsEnabled = request is not null;
        DeclineButton.IsEnabled = request is not null;
    }

    private async void OnApproveClicked(object? sender, EventArgs e)
    {
        if (_busy)
            return;

        var chosen = _checks.Where(c => c.Value.IsChecked).Select(c => c.Key).ToList();
        if (chosen.Count == 0 && _checks.Count > 0)
        {
            var anyway = await _popups.ConfirmWarningAsync(
                "They'll be in the family but won't see anybody's readings or get any alerts. You can share people with them later.",
                "Let them in with no access?", "Yes, just the family", "Go back");
            if (!anyway)
                return;
        }

        _busy = true;
        ApproveButton.IsEnabled = false;
        try
        {
            await _api.ApproveJoinRequestAsync(_organizationId, _requestId, new ApproveJoinRequest
            {
                CardiMemberIds = chosen,
                // Always a member. See the card on this page, and D-14.
                Role = "member",
                ReceiveAlerts = AlertsSwitch.IsToggled,
            });

            await _popups.ShowInfoAsync(
                chosen.Count == 0
                    ? "They're in the family. Nothing is shared with them yet."
                    : $"They're in, and can now see {(chosen.Count == 1 ? "the person" : $"the {chosen.Count} people")} you chose.",
                "Done");
            await Shell.Current.GoToAsync(AppShell.FamilyRoute);
        }
        catch (ApiException ex)
        {
            // MEMBER_LIMIT_REACHED lands here as a 422 whose message names the count and the
            // ceiling; it is a real state on dev, where old family subscriptions still carry
            // MaxUsers = 1, so it is shown as the server wrote it rather than flattened.
            await _popups.ShowWarningAsync(ex.Message, "Couldn't let them in");
        }
        finally
        {
            _busy = false;
            ApproveButton.IsEnabled = true;
        }
    }

    private async void OnDeclineClicked(object? sender, EventArgs e)
    {
        if (_busy)
            return;

        var confirmed = await _popups.ConfirmWarningAsync(
            "They won't be told why, and can ask again later.",
            "Say no?", "Decline", "Cancel");
        if (!confirmed)
            return;

        _busy = true;
        DeclineButton.IsEnabled = false;
        try
        {
            await _api.DeclineJoinRequestAsync(_organizationId, _requestId);
            await Shell.Current.GoToAsync(AppShell.FamilyRoute);
        }
        catch (ApiException ex)
        {
            await _popups.ShowWarningAsync(ex.Message, "Couldn't decline that");
        }
        finally
        {
            _busy = false;
            DeclineButton.IsEnabled = true;
        }
    }

    private void SetState(bool loading = false, bool loaded = false, bool error = false)
    {
        SkeletonPanel.IsVisible = loading;
        ContentPanel.IsVisible = loaded;
        ErrorPanel.IsVisible = error;
        ActionsPanel.IsVisible = loaded;
    }
}
