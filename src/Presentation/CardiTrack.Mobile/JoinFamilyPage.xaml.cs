using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Family;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

/// <summary>
/// Asking to join a family by its Family ID, or by the link that carries one (Story 4.7).
/// </summary>
/// <remarks>
/// The field takes both, because they are the same value: D-11 settled that the link is a
/// convenience wrapper around the identifier rather than a second, stronger channel. An
/// invitation token can be pasted here too and is routed to the invitation, which is a different
/// act — it names one member and grants access on acceptance rather than asking anybody.
/// </remarks>
public partial class JoinFamilyPage : ContentPage
{
    public const string Route = "joinfamily";

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;
    private bool _busy;

    public JoinFamilyPage(ICardiTrackApiClient api, IPopupService popups)
    {
        InitializeComponent();
        _api = api;
        _popups = popups;

        NotesList.Apply(
        [
            "Their admin decides whether to let you in, and which of the people they watch you can see.",
            "Nothing about their family is shown to you until they say yes.",
            "You keep your own family, if you have one — joining theirs does not replace it.",
        ]);
    }

    private async void OnBackTapped(object? sender, TappedEventArgs e) =>
        await this.GoBackAsync(AppShell.FamilyRoute);

    private void OnAskCompleted(object? sender, EventArgs e) => _ = AskSafelyAsync();

    private void OnAskClicked(object? sender, EventArgs e) => _ = AskSafelyAsync();

    /// <summary>
    /// Started from a tap, so nothing above it can catch a failure: without this a code that
    /// could not be acted on would leave the caregiver pressing a button that never answers.
    /// </summary>
    private async Task AskSafelyAsync()
    {
        try
        {
            await AskAsync();
        }
        catch (Exception ex)
        {
            await _popups.ShowWarningAsync(ex.Message, "Couldn't open that");
        }
    }

    private async Task AskAsync()
    {
        if (_busy)
            return;

        var parsed = JoinInput.Parse(CodeEntry.Text);
        if (parsed is null)
        {
            await _popups.ShowWarningAsync(
                "That doesn't look like a Family ID or an invitation link. A Family ID is eight characters, like KTR7-M2Q9.",
                "Check that code");
            return;
        }

        // An invitation names one member and grants access on acceptance; asking to join a
        // family is a different act. The token goes to the invitation screen, which is reached
        // from the Family tab so it survives this page being popped on the way.
        if (parsed is JoinInput.Invitation invitation)
        {
            await Shell.Current.GoToAsync(
                $"{AppShell.FamilyRoute}/{AcceptInvitePage.Route}?token={Uri.EscapeDataString(invitation.Token)}");
            return;
        }

        _busy = true;
        AskButton.IsEnabled = false;
        try
        {
            await _api.RequestToJoinFamilyAsync(((JoinInput.FamilyCode)parsed).FamilyId);

            // Deliberately the same sentence whatever the server found. An unknown code, a
            // malformed one and a family the caller is already in are one answer here, as they
            // are on the wire — anything else would turn this screen into the oracle the
            // endpoint refuses to be.
            await _popups.ShowInfoAsync(
                "If that code is right, their admin has been asked. They'll let you know.",
                "We've sent your ask");
            await Shell.Current.GoToAsync(AppShell.FamilyRoute);
        }
        catch (ApiException ex)
        {
            await _popups.ShowWarningAsync(ex.Message, "Couldn't ask to join");
        }
        finally
        {
            _busy = false;
            AskButton.IsEnabled = true;
        }
    }
}
