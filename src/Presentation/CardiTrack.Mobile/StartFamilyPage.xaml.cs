using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Family;
using CardiTrack.Mobile.Onboarding;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

/// <summary>
/// Starting a family of one's own, from the Family tab or its drawer.
/// </summary>
/// <remarks>
/// <para>
/// There is nothing to name here, and that is the point of D-12: a guest has no organization and
/// no trial, and both are created at the moment they add their first CardiMember — so this page
/// explains that and hands over to the add-member wizard, which is the act that actually starts
/// the family. A form that asked for a family name first would create the expectation that
/// something happened when the button was pressed, and nothing does.
/// </para>
/// <para>
/// It is reachable by a caregiver who already has a family, from the drawer's foot. That is not
/// an error: adding another CardiMember is how a family grows, and the wizard handles the case.
/// </para>
/// </remarks>
public partial class StartFamilyPage : ContentPage
{
    public const string Route = "startfamily";

    private readonly IPopupService _popups;
    private bool _wizardActive;

    public StartFamilyPage(IPopupService popups)
    {
        InitializeComponent();
        _popups = popups;

        StepsList.Apply(
        [
            "You add the person you want to watch over — their name, their date of birth, and how you know them.",
            "We create your family at that moment, with you as its admin.",
            "You connect their watch, and CardiTrack starts learning what a normal day looks like for them.",
            "You can invite anybody else who helps look after them, and share your Family ID so they can ask to join.",
        ]);

        TrialLabel.Text = FamilyCopy.TrialStartsWithFirstMember
            + " Until then there is nothing to pay for and nothing running.";
    }

    private async void OnBackTapped(object? sender, TappedEventArgs e) =>
        await this.GoBackAsync(AppShell.FamilyRoute);

    private async void OnNotNowClicked(object? sender, EventArgs e) =>
        await this.GoBackAsync(AppShell.FamilyRoute);

    /// <summary>
    /// The add-member wizard, run modally over this page — the same one the dashboard's empty
    /// state opens, so a family started here goes through exactly the steps a family started at
    /// onboarding does.
    /// </summary>
    private async void OnAddMemberClicked(object? sender, EventArgs e)
    {
        if (_wizardActive)
            return;
        _wizardActive = true;
        try
        {
            var result = await WizardLauncher.RunModalAsync(Navigation, member: null);
            if (result.ExitedToDashboard)
                return;

            // Back to the tab either way: if they added somebody the tab now has a family to
            // draw, and if they backed out it is the screen they came from.
            await Shell.Current.GoToAsync(AppShell.FamilyRoute);
        }
        catch (Exception ex)
        {
            // async void: anything escaping here takes the app down rather than reaching a
            // caller, and RunModalAsync rethrows when the modal cannot be pushed.
            await _popups.ShowErrorAsync(ex.Message, "Couldn't start that");
        }
        finally
        {
            _wizardActive = false;
        }
    }
}
