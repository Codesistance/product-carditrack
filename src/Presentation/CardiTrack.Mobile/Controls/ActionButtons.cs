using CardiTrack.Mobile.Core.Forms;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// Dresses a <see cref="Button"/> as one of the action buttons (Styles.xaml ActionButton*) from
/// its <see cref="ActionLook"/> — the style for its colour and the icon beside its word.
/// </summary>
public static class ActionButtons
{
    /// <summary>Styles <paramref name="button"/> from the words on it; see <see cref="ActionLooks.For"/>.</summary>
    public static void Dress(Button button, bool isDismiss = false) =>
        Dress(button, ActionLooks.For(button.Text, isDismiss));

    public static void Dress(Button button, ActionLook look)
    {
        var resources = Microsoft.Maui.Controls.Application.Current!.Resources;
        button.Style = (Style)resources[look.Tone switch
        {
            ActionTone.Green => "ActionButtonGreen",
            ActionTone.Red => "ActionButtonRed",
            ActionTone.Dark => "ActionButtonDark",
            ActionTone.Amber => "ActionButtonAmber",
            _ => "ActionButtonBlue",
        }];
        button.ImageSource = look.Icon is { } icon ? ImageSource.FromFile(icon) : null;
    }
}
