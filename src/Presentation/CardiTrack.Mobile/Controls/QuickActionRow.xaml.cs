using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile.Controls;

/// <summary>Who a <see cref="QuickActionRow"/> is acting on, and the numbers it can reach them by.</summary>
/// <remarks>
/// Its own type rather than either response DTO: the Dashboard binds this from
/// <c>DashboardResponse</c> and the alert detail from <c>AlertDetailResponse</c>, and the row
/// should not have to know that two different payloads carry the same five facts.
/// </remarks>
public sealed record QuickActionTarget(
    Guid CardiMemberId,
    string? Name,
    string? Phone,
    string? EmergencyContactPhone,
    string? EmergencyContactName);

/// <summary>
/// The CardiMember quick-action row — SOS, Call, Message, Details — shared by the Dashboard's
/// member card and the alert detail screen.
/// </summary>
/// <remarks>
/// <para>
/// Extracted rather than copied. The alert detail screen had grown its own pair of full-width
/// Call/Message buttons with their own wording, their own availability rules and its own copy of
/// the "no number yet" offer, so the same two actions looked and behaved differently depending on
/// which screen a caregiver reached them from. There is one right answer to "how do I contact this
/// person", and this is now the only place it is written down.
/// </para>
/// <para>
/// Takes <see cref="IPopupService"/> through <see cref="Apply"/> rather than resolving it: a
/// control built by XAML has no constructor injection, and reaching into the service provider from
/// here would hide a dependency the hosting page already holds.
/// </para>
/// </remarks>
public partial class QuickActionRow : ContentView
{
    /// <summary>
    /// A tile with no number behind it dims but stays tappable — see the dim-not-hide note in the
    /// XAML. Shared with the pages so a disabled action reads the same everywhere.
    /// </summary>
    public const double UnavailableActionOpacity = 0.4;

    private IPopupService? _popups;
    private QuickActionTarget? _target;

    public QuickActionRow()
    {
        InitializeComponent();
    }

    /// <summary>Binds the row to one CardiMember and applies each tile's availability.</summary>
    public void Apply(QuickActionTarget target, IPopupService popups)
    {
        _target = target;
        _popups = popups;

        var firstName = NameFormatting.FirstName(target.Name);
        var hasPhone = !string.IsNullOrWhiteSpace(target.Phone);
        var hasEmergency = !string.IsNullOrWhiteSpace(target.EmergencyContactPhone);

        CallAction.Opacity = hasPhone ? 1 : UnavailableActionOpacity;
        MessageAction.Opacity = hasPhone ? 1 : UnavailableActionOpacity;
        EmergencyCallAction.Opacity = hasEmergency ? 1 : UnavailableActionOpacity;

        // Distinct verbs per tile — Message doesn't call, so sharing one "Calls..." tooltip
        // between both would misdescribe it.
        var noPhone = NoPhoneMessage(firstName);
        ToolTipProperties.SetText(CallAction, hasPhone ? $"Calls {firstName} directly." : noPhone);
        ToolTipProperties.SetText(MessageAction, hasPhone ? $"Messages {firstName} directly." : noPhone);
        ToolTipProperties.SetText(EmergencyCallAction, hasEmergency
            ? string.IsNullOrWhiteSpace(target.EmergencyContactName)
                ? $"Calls {firstName}'s emergency contact."
                : $"Calls {target.EmergencyContactName}, {firstName}'s emergency contact."
            : NoEmergencyContactMessage(firstName));
    }

    // The nameless branch drops the noun rather than substituting one. Every stand-in available
    // here is either internal vocabulary ("this CardiMember") or a greeting-card relationship
    // noun ("your loved one"), and the caregiver is looking at one specific person either way —
    // the sentence reads better with nothing in that slot than with the wrong word in it.
    private static string NoPhoneMessage(string firstName) =>
        string.IsNullOrWhiteSpace(firstName)
            ? "Add a phone number to call or message them from here."
            : $"Add a phone number for {firstName} to call or message them from here.";

    private static string NoEmergencyContactMessage(string firstName) =>
        string.IsNullOrWhiteSpace(firstName)
            ? "Add an emergency contact number to call from here."
            : $"Add an emergency contact number for {firstName} to call from here.";

    // Question forms of the two messages above. The statements are what a tooltip says about a
    // dimmed tile; these are what the tile asks when it is actually tapped.
    private static string AddPhonePrompt(string firstName) =>
        string.IsNullOrWhiteSpace(firstName)
            ? "Would you like to add a phone number, so you can call or message them from here?"
            : $"Would you like to add a phone number for {firstName}, so you can call or message them from here?";

    private static string AddEmergencyContactPrompt(string firstName) =>
        string.IsNullOrWhiteSpace(firstName)
            ? "Would you like to add an emergency contact number, so you can call them from here?"
            : $"Would you like to add an emergency contact number for {firstName}, so you can call them from here?";

    private async void OnCallTapped(object? sender, EventArgs e)
    {
        if (_target is not { } target)
            return;

        if (string.IsNullOrWhiteSpace(target.Phone))
        {
            await OfferToAddNumberAsync(
                AddPhonePrompt(NameFormatting.FirstName(target.Name)),
                EditCardiMemberPage.FocusPhone);
            return;
        }

        try
        {
            PhoneDialer.Default.Open(target.Phone);
        }
        catch (Exception)
        {
            await ShowWarningAsync("Phone calls aren't supported on this device.");
        }
    }

    private async void OnMessageTapped(object? sender, EventArgs e)
    {
        if (_target is not { } target)
            return;

        if (string.IsNullOrWhiteSpace(target.Phone))
        {
            await OfferToAddNumberAsync(
                AddPhonePrompt(NameFormatting.FirstName(target.Name)),
                EditCardiMemberPage.FocusPhone);
            return;
        }

        try
        {
            await Sms.Default.ComposeAsync(new SmsMessage(string.Empty, target.Phone));
        }
        catch (Exception)
        {
            await ShowWarningAsync("Messaging isn't supported on this device.");
        }
    }

    private async void OnEmergencyCallTapped(object? sender, EventArgs e)
    {
        if (_target is not { } target)
            return;

        if (string.IsNullOrWhiteSpace(target.EmergencyContactPhone))
        {
            await OfferToAddNumberAsync(
                AddEmergencyContactPrompt(NameFormatting.FirstName(target.Name)),
                EditCardiMemberPage.FocusEmergencyPhone);
            return;
        }

        try
        {
            PhoneDialer.Default.Open(target.EmergencyContactPhone);
        }
        catch (Exception)
        {
            await ShowWarningAsync("Phone calls aren't supported on this device.");
        }
    }

    private async void OnViewDetailsTapped(object? sender, EventArgs e)
    {
        if (_target is { } target)
            await Shell.Current.GoToAsync($"{CardiMemberDetailPage.Route}?memberId={target.CardiMemberId}");
    }

    /// <summary>
    /// The single answer to "there is no number here yet": ask, then open the profile form so the
    /// caregiver can add one. Offering the fix beats reporting the gap — they reached for this
    /// tile to make contact, and a dialog that only explains leaves them to go find the form
    /// themselves. Declining costs nothing, and the form arrives pre-filled from the saved
    /// profile, so in the ordinary case the number is all that is left to type — it still
    /// validates the fields the API requires (name, date of birth, relationship), which a
    /// profile saved before those rules tightened could trip.
    /// </summary>
    /// <param name="focus">
    /// Which number field to land on — member phone or emergency contact — so the caret is on
    /// the gap that triggered this offer rather than at the top of the form.
    /// </param>
    private async Task OfferToAddNumberAsync(string prompt, string focus)
    {
        if (_popups is not { } popups || _target is not { } target)
            return;

        var addNow = await popups.ConfirmInfoAsync(prompt, "No number yet", "Add number", "Not now");
        if (addNow)
            await Shell.Current.GoToAsync(
                $"{EditCardiMemberPage.Route}?memberId={target.CardiMemberId}&focus={focus}");
    }

    private Task ShowWarningAsync(string message) =>
        _popups?.ShowWarningAsync(message) ?? Task.CompletedTask;
}
