using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Devices;
using CardiTrack.Mobile.Core.Family;
using CardiTrack.Mobile.Core.Forms;
using CardiTrack.Mobile.Core.Navigation;
using CardiTrack.Mobile.Core.Offline;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

/// <summary>
/// "Who can see &lt;member&gt;" (Story 4.1): the invitations issued for one CardiMember, their
/// state, and the one that was just minted with its link.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="Onboarding.InviteWaitPage"/>, the shipped invitation primitive, down to the
/// rule that matters most: <c>CaregiverInviteResponse.Url</c> comes back exactly once, on create,
/// and is kept in a field rather than re-read from the list — a refresh that took the link away
/// would be a link the caregiver had not sent yet and could not get back.
/// </para>
/// <para>
/// CardiTrack sends nothing: the link goes to the caregiver's own share sheet, their choice of
/// app, their contact list, and we never learn who it went to. That is why there is no recipient
/// field on this page and no email anywhere in the flow.
/// </para>
/// <para>
/// There is no role choice. An invitation always admits as a member (see the API doc), and
/// handing the family over is its own screen.
/// </para>
/// </remarks>
[QueryProperty(nameof(MemberId), "memberId")]
[QueryProperty(nameof(MemberName), "name")]
public partial class CaregiverInvitesPage : ContentPage
{
    public const string Route = "caregiverinvites";

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;

    private readonly LoadGate _gate = new();
    private readonly RefreshFeedback _feedback;
    private readonly MemberRoute _member = new();

    private string _memberName = string.Empty;
    private string? _freshLink;
    private bool _busy;
    private bool _returningFromPopup;

    public CaregiverInvitesPage(ICardiTrackApiClient api, IPopupService popups)
    {
        InitializeComponent();
        _api = api;
        _popups = popups;
        // No "Updating…" overlay on this page: the list is short, and an overlay over three
        // rows reads as a page that is stuck rather than one that is checking.
        _feedback = new RefreshFeedback(SavedBanner, overlay: null);
    }

    public string MemberId
    {
        set => _member.Accept(value);
    }

    public string MemberName
    {
        set => _memberName = Uri.UnescapeDataString(value ?? string.Empty);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_popups.IsShowing || _returningFromPopup)
        {
            _returningFromPopup = false;
            return;
        }

        RouteArrival.WhenRouteHasLanded(this, () => _ = LoadAsync());
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _returningFromPopup = _popups.IsShowing;
    }

    private void OnRetryClicked(object? sender, EventArgs e) => _ = LoadAsync();

    private async void OnPullToRefresh(object? sender, EventArgs e)
    {
        await LoadAsync();
        Refresher.IsRefreshing = false;
    }

    private async void OnBackTapped(object? sender, TappedEventArgs e) =>
        await this.GoBackAsync(AppShell.DashboardRoute);

    private async Task LoadAsync()
    {
        if (_member.IsMissing)
        {
            ErrorDetailLabel.Text = MemberRoute.MissingMessage;
            SetState(error: true);
            return;
        }

        if (_gate.IsLoading)
            return;
        var ticket = _gate.Begin();
        var memberId = _member.Id;

        var cold = !ContentPanel.IsVisible;
        if (cold)
            SetState(loading: true);

        try
        {
            var outcome = await SnapshotRefresh.RunAsync(
                _api, _gate, ticket,
                peek: cold ? ct => _api.PeekCaregiverInvitesAsync(memberId, ct) : null,
                fetch: ct => _api.GetCaregiverInvitesAsync(memberId, ct),
                render: invites =>
                {
                    Apply(invites);
                    SetState(loaded: true);
                },
                _feedback);

            switch (outcome.Result)
            {
                case RefreshResult.Superseded:
                    return;
                case RefreshResult.NothingAndFailed:
                    ErrorDetailLabel.Text = outcome.Error!.Message;
                    SetState(error: true);
                    return;
            }
        }
        finally
        {
            if (_gate.IsCurrent(ticket))
                Refresher.IsRefreshing = false;
            _gate.Release(ticket);
        }
    }

    private void Apply(IReadOnlyList<CaregiverInviteResponse> invites)
    {
        var first = NameFormatting.FirstName(_memberName);
        var who = string.IsNullOrWhiteSpace(first) ? "them" : first;
        HeaderTitle.Text = string.IsNullOrWhiteSpace(first) ? "Who can see them" : $"Who can see {first}";
        HeaderSubtitle.Text = "The people helping you watch over them";

        GrantsList.Apply(
        [
            $"They see how {who} is doing, and get the alerts about {who}.",
            $"They join your family as a member — they can't invite anybody else or change what the rest of you see.",
            "They choose, as they accept, whether an alert nobody else answered wakes them at night.",
        ]);

        EmptyDetailLabel.Text =
            $"Invite whoever else looks after {who}. They'll need the link you send them, and an account of their own.";

        var ordered = invites
            .OrderByDescending(FamilyCopy.IsLive)
            .ThenByDescending(i => i.ExpiresAt)
            .ToList();

        EmptyCard.IsVisible = ordered.Count == 0;
        InvitesHost.Clear();
        foreach (var invite in ordered)
            InvitesHost.Add(InviteCard(invite));
    }

    private View InviteCard(CaregiverInviteResponse invite)
    {
        var stack = new VerticalStackLayout { Spacing = 8 };
        stack.Add(new Label
        {
            // The invitation carries no name: nothing on it says who it was sent to, because
            // nothing ever asked. The admin knows who they gave it to.
            Text = FamilyCopy.IsLive(invite) ? "Waiting to be accepted" : "Invitation",
            Style = Named("Body1SemiBoldDark"),
        });
        stack.Add(new Label
        {
            Text = FamilyCopy.InviteStatusLine(invite, DateTime.UtcNow),
            Style = Named("Body2"),
            LineBreakMode = LineBreakMode.WordWrap,
        });

        if (FamilyCopy.IsLive(invite))
        {
            var revoke = new Button
            {
                Text = "Cancel this invitation",
                Style = Named("SecondaryOutlineButton"),
            };
            revoke.Clicked += (_, _) => _ = RevokeAsync(invite);
            stack.Add(revoke);
        }

        return new Border { Style = Named("ElevatedCard"), Content = stack };
    }

    private async void OnInviteClicked(object? sender, EventArgs e)
    {
        if (_busy || _member.IsMissing)
            return;

        var first = NameFormatting.FirstName(_memberName);
        var who = string.IsNullOrWhiteSpace(first) ? "this person" : first;
        var confirmed = await _popups.ConfirmInfoAsync(
            $"We'll make a link that lets one person see how {who} is doing and get their alerts. "
            + "Send it to somebody you trust — anybody who opens it and signs in can accept it.",
            "Invite somebody", "Make the link", "Cancel");
        if (!confirmed)
            return;

        _busy = true;
        InviteButton.IsEnabled = false;
        try
        {
            var invite = await _api.CreateCaregiverInviteAsync(_member.Id, new CreateCaregiverInviteRequest
            {
                // What the invitation grants. Both true: an invitation exists so that somebody
                // can take a share of the watching, and a caregiver who sees the data but never
                // hears an alert is not a second pair of eyes at 3am.
                CanViewHealthData = true,
                ReceiveAlerts = true,
                Role = "member",
            });

            ShowFreshLink(invite);
            await LoadAsync();
        }
        catch (ApiException ex)
        {
            // MEMBER_LIMIT_REACHED arrives as a 422 naming the count and the ceiling — a real
            // state on dev, where family subscriptions minted before the change still carry
            // MaxUsers = 1.
            await _popups.ShowWarningAsync(ex.Message, "Couldn't make an invitation");
        }
        finally
        {
            _busy = false;
            InviteButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Puts the one-time link on screen, with its QR code. Kept in a field because the API will
    /// not return it again.
    /// </summary>
    private void ShowFreshLink(CaregiverInviteResponse invite)
    {
        _freshLink = invite.Url;
        NewInviteCard.IsVisible = !string.IsNullOrWhiteSpace(_freshLink);
        if (!NewInviteCard.IsVisible)
            return;

        NewInviteDetailLabel.Text =
            $"This link works for {FamilyCopy.Remaining(invite.ExpiresAt, DateTime.UtcNow)} and can be used once. "
            + "We don't send it for you — that way we never learn who you sent it to.";

        LinkLabel.Text = _freshLink;
        QrImage.Source = InviteQrCode.Render(_freshLink) is { } png
            ? ImageSource.FromStream(() => new MemoryStream(png))
            : null;
        QrImage.IsVisible = QrImage.Source is not null;
    }

    private async void OnCopyLinkClicked(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_freshLink))
            return;

        await Clipboard.Default.SetTextAsync(_freshLink);
        CopyLinkButton.Text = "Copied";
        await Task.Delay(TimeSpan.FromSeconds(2));
        CopyLinkButton.Text = "Copy link";
    }

    private async void OnShareLinkClicked(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_freshLink))
            return;

        var first = NameFormatting.FirstName(_memberName);
        var who = string.IsNullOrWhiteSpace(first) ? "someone I look after" : first;
        try
        {
            await Share.Default.RequestAsync(new ShareTextRequest
            {
                Title = "Send this invitation",
                Subject = $"Help me keep an eye on {who}",
                Text = $"I'd like you to help me keep an eye on {who} with CardiTrack. "
                       + $"Open this and sign in to accept.{Environment.NewLine}{_freshLink}",
            });
        }
        catch (Exception)
        {
            await _popups.ShowWarningAsync("Sharing isn't supported on this device.");
        }
    }

    private async Task RevokeAsync(CaregiverInviteResponse invite)
    {
        if (_busy)
            return;

        var confirmed = await _popups.ConfirmWarningAsync(
            "The link stops working straight away. If they have already accepted it, this changes nothing — remove them from the family instead.",
            "Cancel this invitation?", "Cancel it", "Keep it");
        if (!confirmed)
            return;

        _busy = true;
        try
        {
            await _api.RevokeCaregiverInviteAsync(_member.Id, invite.InviteId);

            // The link on screen may be the one just cancelled, and a dead link is worse than
            // none: it looks sendable.
            _freshLink = null;
            NewInviteCard.IsVisible = false;
            await LoadAsync();
        }
        catch (ApiException ex)
        {
            await _popups.ShowWarningAsync(ex.Message, "Couldn't cancel it");
        }
        finally
        {
            _busy = false;
        }
    }

    private void SetState(bool loading = false, bool loaded = false, bool error = false)
    {
        SkeletonPanel.IsVisible = loading;
        ContentPanel.IsVisible = loaded;
        ErrorPanel.IsVisible = error;
        ActionsPanel.IsVisible = loaded;
    }

    private static Style? Named(string key) =>
        Microsoft.Maui.Controls.Application.Current?.Resources.TryGetValue(key, out var found) == true
            ? found as Style
            : null;
}
