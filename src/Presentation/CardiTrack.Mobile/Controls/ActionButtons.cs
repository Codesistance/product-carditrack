using CardiTrack.Mobile.Core.Forms;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// Dresses an <see cref="AppButton"/> from its <see cref="ActionLook"/> — the tone for its meaning
/// and the glyph beside its word — for the popups whose buttons are worded by their caller.
/// </summary>
/// <remarks>
/// <see cref="ActionLooks"/> still speaks the five colours the popups first had, one per meaning.
/// The unified button (2026-09-26) has fewer tones, so two of those meanings share one: Yes is the
/// primary action like Save, and Undo is the everyday tonal action rather than a colour of its own.
/// The words decide the tone exactly as before; only the palette they land on changed.
/// </remarks>
public static class ActionButtons
{
    /// <summary>Dresses <paramref name="button"/> from the words on it; see <see cref="ActionLooks.For"/>.</summary>
    public static void Dress(AppButton button, bool isDismiss = false) =>
        Dress(button, ActionLooks.For(button.Text, isDismiss));

    public static void Dress(AppButton button, ActionLook look)
    {
        button.Tone = look.Tone switch
        {
            ActionTone.Red => AppButtonTone.Danger,
            ActionTone.Dark => AppButtonTone.Neutral,
            ActionTone.Amber => AppButtonTone.Tonal,
            _ => AppButtonTone.Primary,
        };

        // The tonal fill is pale, so it takes the blue-ink version of a glyph rather than the
        // white one the solid tones carry.
        var icon = look.Icon;
        if (icon is not null && button.Tone == AppButtonTone.Tonal)
            icon = icon.Replace(".svg", "_tint.svg", StringComparison.Ordinal);
        button.Icon = icon is not null ? ImageSource.FromFile(icon) : null;
    }
}
