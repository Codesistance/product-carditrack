using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Controls;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Family;
using CardiTrack.Mobile.Core.Forms;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

/// <summary>
/// An admin handing the family, and its plan, to somebody else — and then leaving (Story 4.8).
/// </summary>
/// <remarks>
/// <para>
/// Two acts, in one journey because they are one intention: <c>PUT /families/{id}/admin</c>
/// promotes the successor and demotes the caller in the same save (D-14 — there is never a moment
/// with two admins or none), and the leave that follows is the ordinary member leave, which is
/// only possible once the first has landed.
/// </para>
/// <para>
/// Leaving is offered, not forced. An admin who wants to stop paying and stay in the family is a
/// real case — D-17's lapse takeover is exactly that, in the other direction — so the successor
/// is promoted first and leaving is a second, separate question.
/// </para>
/// </remarks>
[QueryProperty(nameof(OrganizationId), "organizationId")]
[QueryProperty(nameof(FamilyName), "name")]
public partial class TransferFamilyAdminPage : ContentPage
{
    public const string Route = "transferfamilyadmin";

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;

    private Guid _organizationId;
    private string _familyName = "this family";
    private bool _busy;
    private bool _loaded;

    public TransferFamilyAdminPage(ICardiTrackApiClient api, IPopupService popups)
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

    public string FamilyName
    {
        set
        {
            var name = Uri.UnescapeDataString(value ?? string.Empty);
            _familyName = string.IsNullOrWhiteSpace(name) ? "this family" : name;
        }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_loaded)
            return;
        _loaded = true;
        RouteArrival.WhenRouteHasLanded(this, () => _ = LoadAsync());
    }

    private void OnRetryClicked(object? sender, EventArgs e) => _ = LoadAsync();

    private async void OnBackTapped(object? sender, TappedEventArgs e) =>
        await this.GoBackAsync(AppShell.FamilyRoute);

    private async Task LoadAsync()
    {
        if (_organizationId == Guid.Empty)
        {
            ErrorDetailLabel.Text = "We couldn't tell which family this is. Open it again from the Family tab.";
            SetState(error: true);
            return;
        }

        HeaderSubtitle.Text = _familyName;
        SetState(loading: true);
        try
        {
            var roster = await _api.GetFamilyMembersAsync(_organizationId);
            Apply(roster);
            SetState(loaded: true);
        }
        catch (ApiException ex)
        {
            ErrorDetailLabel.Text = ex.Message;
            SetState(error: true);
        }
    }

    private void Apply(IReadOnlyList<FamilyMemberSummary> roster)
    {
        ConsequencesList.Apply(
        [
            $"They become the admin of {_familyName} and you become an ordinary member, in one step.",
            "They decide who joins, who is removed, and what everybody can see.",
            "The plan goes with it. When billing arrives, they are the one who pays for this family.",
            "You keep seeing everybody you can see today, unless you leave as well.",
        ]);

        var others = roster.Where(m => !m.IsYou).ToList();
        SoloCard.IsVisible = others.Count == 0;
        SuccessorSection.IsVisible = others.Count > 0;

        SuccessorHost.Clear();
        foreach (var person in others)
        {
            var row = new Grid
            {
                ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
                ColumnSpacing = 12,
                MinimumHeightRequest = 56,
            };

            var avatar = new MemberAvatar { BoxWidth = 40, VerticalOptions = LayoutOptions.Center };
            avatar.Apply(person.Name, null);
            row.Add(avatar, 0, 0);

            var text = new VerticalStackLayout { Spacing = 1, VerticalOptions = LayoutOptions.Center };
            text.Add(new Label { Text = person.Name, Style = Named("Body1SemiBoldDark") });
            text.Add(new Label
            {
                Text = $"In the family since {person.JoinedDate:d MMM yyyy}",
                Style = Named("Body2"),
            });
            row.Add(text, 1, 0);

            var choose = new AppButton
            {
                Text = "Hand over",
                Tone = AppButtonTone.Tonal,
                Size = AppButtonSize.S,
                VerticalOptions = LayoutOptions.Center,
            };
            choose.Clicked += (_, _) => _ = HandOverAsync(person);
            row.Add(choose, 2, 0);

            SuccessorHost.Add(new Border
            {
                Style = Named("ElevatedCard"),
                Padding = new Thickness(14, 10),
                Content = row,
            });
        }
    }

    private async Task HandOverAsync(FamilyMemberSummary successor)
    {
        if (_busy)
            return;

        var first = NameFormatting.FirstName(successor.Name);
        var confirmed = await _popups.ConfirmWarningAsync(
            $"{successor.Name} becomes the admin of {_familyName} and you become a member. "
            + "You can't undo this yourself — only the new admin can hand it back.",
            $"Make {first} the admin?", "Hand it over", "Cancel");
        if (!confirmed)
            return;

        _busy = true;
        try
        {
            await _api.TransferFamilyAdminAsync(_organizationId, successor.UserId);

            // Leaving is the second question, asked only once the first has actually landed —
            // an admin cannot leave, so asking beforehand would have offered something that
            // could not be done.
            var leave = await _popups.ConfirmInfoAsync(
                $"{first} runs {_familyName} now. Do you want to leave it as well, or stay on as a member?",
                "They're the admin now", "Leave the family", "Stay as a member");

            if (leave)
                await LeaveAsync();
            else
                await Shell.Current.GoToAsync(AppShell.FamilyRoute);
        }
        catch (ApiException ex)
        {
            await _popups.ShowWarningAsync(ex.Message, "Couldn't hand it over");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Leaves, and only then moves on. A leave the server refused keeps the caregiver on this page:
    /// they are still a member, the tab they would have been sent to would draw them as one, and
    /// the refusal's own sentence is what tells them what to do next.
    /// </summary>
    private async Task LeaveAsync()
    {
        try
        {
            await _api.LeaveFamilyAsync(_organizationId);
        }
        catch (ApiException ex)
        {
            await _popups.ShowWarningAsync(ex.Message, "Couldn't leave");
            return;
        }

        await _popups.ShowInfoAsync(
            $"You've left {_familyName}. You no longer see the people it watches.",
            "Done");
        await Shell.Current.GoToAsync(AppShell.FamilyRoute);
    }

    private async void OnAccountDeletionClicked(object? sender, EventArgs e) =>
        await Shell.Current.GoToAsync(AppShell.SettingsRoute);

    private void SetState(bool loading = false, bool loaded = false, bool error = false)
    {
        SkeletonPanel.IsVisible = loading;
        ContentPanel.IsVisible = loaded;
        ErrorPanel.IsVisible = error;
    }

    private static Style? Named(string key) =>
        Microsoft.Maui.Controls.Application.Current?.Resources.TryGetValue(key, out var found) == true
            ? found as Style
            : null;
}
