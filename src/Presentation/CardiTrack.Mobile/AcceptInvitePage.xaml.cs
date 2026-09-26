using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Auth;
using CardiTrack.Mobile.Core.Family;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

/// <summary>
/// The invitee's half of a caregiver invitation (Story 4.2 / A3): what it is, the forced
/// quiet-hours choice (D-8), and the accept that writes the grant.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing is shown before sign-in.</strong> <c>GET /caregiver-invites/{token}</c> is
/// authorized on purpose, so that a guessed token never yields a member's first name. A caregiver
/// arriving without a session sees a generic card and a way to sign in; only afterwards does the
/// page load the invitation and name anybody.
/// </para>
/// <para>
/// <strong>An invitation always admits as a Member.</strong> There is no role choice here and
/// must not be one: handing a family over is <c>PUT /families/{id}/admin</c>, a separate
/// deliberate act, not a field on a message sent a week earlier.
/// </para>
/// <para>
/// The quiet-hours answer is written to the caregiver's own notification preferences before the
/// invitation is redeemed. That order is deliberate: the fan-out rung reads the preference, and a
/// grant that existed for even a moment with the wrong answer is a grant that could be woken —
/// or not woken — against what they just chose. The PUT is a full document, so the current
/// preferences are read first and sent back with one field changed.
/// </para>
/// </remarks>
[QueryProperty(nameof(Token), "token")]
public partial class AcceptInvitePage : ContentPage
{
    public const string Route = "acceptinvite";

    private readonly ICardiTrackApiClient _api;
    private readonly IAuthService _auth;
    private readonly IPopupService _popups;

    private string _token = string.Empty;
    private CaregiverInviteView? _invite;
    private bool? _pierceQuietHours;
    private bool _busy;
    private bool _loaded;

    public AcceptInvitePage(ICardiTrackApiClient api, IAuthService auth, IPopupService popups)
    {
        InitializeComponent();
        _api = api;
        _auth = auth;
        _popups = popups;
        QuietHoursQuestionLabel.Text = FamilyCopy.QuietHoursQuestion;
    }

    public string Token
    {
        set => _token = Uri.UnescapeDataString(value ?? string.Empty).Trim();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_loaded)
            return;
        _loaded = true;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (string.IsNullOrEmpty(_token))
        {
            ShowGeneric(
                "That link is missing its invitation code. Ask whoever sent it to share it again.",
                "Back", GenericAction.Back);
            return;
        }

        // Signed out: the generic page, and nothing that could name a member. A silent sign-in
        // is tried first, because a caregiver who opened this from a link a week after their
        // last visit has a refresh token and no live session, and sending them to a password
        // form they did not need is the commonest way to lose them here.
        if (!await _auth.TrySilentSignInAsync())
        {
            ShowGeneric(
                "Sign in, or create an account with the email they invited, and we'll show you who this is about.",
                "Sign in", GenericAction.SignIn);
            return;
        }

        SetState(loading: true);
        try
        {
            _invite = await _api.ViewCaregiverInviteAsync(_token);
            Apply(_invite);
            SetState(loaded: true);
        }
        catch (ApiException ex) when (ex.IsNotFound)
        {
            // Unknown, expired, spent and withdrawn are one answer from the server and one
            // answer here: a page that distinguished them would say whether a guessed token had
            // ever been real.
            ShowGeneric(
                "That invitation is no longer available. It may have been used already, cancelled, or run out of time.",
                "Back", GenericAction.Back);
        }
        catch (ApiException ex)
        {
            ShowGeneric(ex.Message, "Try again", GenericAction.Retry);
        }
    }

    private void Apply(CaregiverInviteView invite)
    {
        var member = string.IsNullOrWhiteSpace(invite.MemberFirstName) ? "somebody" : invite.MemberFirstName;
        var inviter = string.IsNullOrWhiteSpace(invite.InviterFirstName) ? "Somebody" : invite.InviterFirstName;

        HeaderTitle.Text = $"{inviter} Invited You";
        HeaderSubtitle.Text = $"To help watch over {member}";

        Avatar.BoxWidth = 52;
        Avatar.Apply(member, null);
        InviteTitleLabel.Text = $"{inviter} would like you to help watch over {member}.";
        InviteExpiryLabel.Text = $"This invitation works for {FamilyCopy.Remaining(invite.ExpiresAt, DateTime.UtcNow)}.";

        // What an invitation grants is fixed by the invitation itself, and the inviter chose it —
        // so the list is built from the flags the view carries, not from a sentence that assumes
        // both. Said as what they will be able to do rather than as flag names.
        GrantsList.Apply(GrantLines(invite, member));

        QuietHoursNoteLabel.Text =
            "You can change this later under Settings, Notifications. If you leave without choosing, "
            + "we hold alerts until your quiet hours end.";
    }

    /// <summary>
    /// Back goes to the dashboard rather than popping: this page is reached from a link, from the
    /// join field, and from the Family tab, and the one thing all three have behind them is the
    /// app itself. Declining is a deliberate answer with its own button — leaving is not one.
    /// </summary>
    private async void OnBackTapped(object? sender, TappedEventArgs e) =>
        await this.GoBackAsync(AppShell.DashboardRoute);

    /// <summary>
    /// One line per grant the invitation carries, and the membership line every invitation ends
    /// with. A view carrying neither flag is one from a server that predates them, not an
    /// invitation that grants nothing — the composer refuses to mint one of those — so it falls
    /// back to the fuller wording rather than a list that says only "you join the family".
    /// </summary>
    private static IReadOnlyList<string> GrantLines(CaregiverInviteView invite, string member)
    {
        var (view, alerts) = invite.CanViewHealthData || invite.ReceiveAlerts
            ? (invite.CanViewHealthData, invite.ReceiveAlerts)
            : (true, true);

        var lines = new List<string>(3);
        if (view)
            lines.Add($"See how {member} is doing — their readings, their alerts and their journal.");
        if (alerts)
        {
            lines.Add(view
                ? $"Get the alerts about {member} on your own phone."
                : $"Get the alerts about {member} on your own phone, and answer them — without a view of the readings behind them.");
        }
        lines.Add("Join their family as a member. Only its admin can invite people or change the plan.");
        return lines;
    }

    private void OnWakeMeTapped(object? sender, TappedEventArgs e) => Choose(true);

    private void OnHoldTapped(object? sender, TappedEventArgs e) => Choose(false);

    private void Choose(bool pierce)
    {
        _pierceQuietHours = pierce;
        WakeMeTick.IsVisible = pierce;
        HoldTick.IsVisible = !pierce;
        AcceptButton.IsEnabled = true;
    }

    private async void OnAcceptClicked(object? sender, EventArgs e)
    {
        if (_busy || _pierceQuietHours is not { } pierce)
            return;

        _busy = true;
        AcceptButton.IsEnabled = false;
        DeclineButton.IsEnabled = false;
        try
        {
            await SaveQuietHoursChoiceAsync(pierce);

            var redemption = await _api.AcceptCaregiverInviteAsync(_token);
            await _popups.ShowInfoAsync(
                redemption.AlreadyHadAccess
                    ? "You already had access to them, so nothing changed."
                    : FamilyCopy.NightCoverageLine(pierce),
                redemption.AlreadyHadAccess ? "You're already watching" : "You're in");

            await Shell.Current.GoToAsync(AppShell.DashboardRoute);
        }
        catch (ApiException ex)
        {
            await _popups.ShowWarningAsync(ex.Message, "Couldn't accept that");
        }
        finally
        {
            _busy = false;
            AcceptButton.IsEnabled = true;
            DeclineButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Writes the answer to this caregiver's own preferences, in their own timezone (D-6).
    /// </summary>
    /// <remarks>
    /// A full-document PUT, so the rest of the preferences are read and sent back unchanged —
    /// anything omitted is reset, and a caregiver accepting an invitation must not silently lose
    /// the quiet hours they set last month.
    /// </remarks>
    private async Task SaveQuietHoursChoiceAsync(bool pierce)
    {
        var current = await _api.GetNotificationPreferencesAsync();
        if (current.EscalatedAlertsPierceQuietHours == pierce)
            return;

        await _api.UpdateNotificationPreferencesAsync(new UpdateNotificationPreferenceRequest
        {
            QuietHoursStart = current.QuietHoursStart,
            QuietHoursEnd = current.QuietHoursEnd,
            ShowDetailsOnLockScreen = current.ShowDetailsOnLockScreen,
            EscalatedAlertsPierceQuietHours = pierce,
            MutedCategories = [.. current.MutedCategories],
        });
    }

    private async void OnDeclineClicked(object? sender, EventArgs e)
    {
        if (_busy)
            return;

        var confirmed = await _popups.ConfirmWarningAsync(
            "We'll let them know you said no. They can invite you again if that was a mistake.",
            "Say no to this invitation?", "No thanks", "Cancel");
        if (!confirmed)
            return;

        _busy = true;
        try
        {
            await _api.DeclineCaregiverInviteAsync(_token);
            await Shell.Current.GoToAsync(AppShell.DashboardRoute);
        }
        catch (ApiException ex)
        {
            await _popups.ShowWarningAsync(ex.Message, "Couldn't do that");
        }
        finally
        {
            _busy = false;
        }
    }

    private enum GenericAction
    {
        SignIn,
        Retry,
        Back,
    }

    private GenericAction _genericAction = GenericAction.Back;

    private void ShowGeneric(string detail, string buttonText, GenericAction action)
    {
        GenericDetailLabel.Text = detail;
        GenericButton.Text = buttonText;
        _genericAction = action;
        SetState(generic: true);
    }

    private async void OnGenericButtonClicked(object? sender, EventArgs e)
    {
        switch (_genericAction)
        {
            case GenericAction.Retry:
                await LoadAsync();
                return;
            case GenericAction.SignIn:
                // The sign-in page is a root swap, not a push: the app has no session, so there
                // is no shell to come back to. The caregiver opens the link again afterwards,
                // which is the same journey the device-connection invitation asks for.
                WindowNavigation.SetRootPage(this, new NavigationPage(new SignInPage()));
                return;
            default:
                await this.GoBackAsync(AppShell.DashboardRoute);
                return;
        }
    }

    private void SetState(bool loading = false, bool loaded = false, bool generic = false)
    {
        SkeletonPanel.IsVisible = loading;
        ContentPanel.IsVisible = loaded;
        GenericPanel.IsVisible = generic;
    }
}
