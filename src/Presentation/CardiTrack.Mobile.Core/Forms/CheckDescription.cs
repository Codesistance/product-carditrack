namespace CardiTrack.Mobile.Core.Forms;

/// <summary>
/// What a screen reader is told about the app's own tick box, kept out of the control so the
/// fallback order can be pinned without a MAUI host.
/// </summary>
/// <remarks>
/// A description the page wrote wins: it is the page's own word for the row, and a page reaches
/// for one exactly when the label beside the box is not plain words — the sign-up form's terms
/// line is two links and a sentence, and "I agree to the Terms of Service and Privacy Policy" is
/// what that row means. Without one the label is the description, which is the ordinary case.
/// Only a box with neither is called what it is, and that is a box a page forgot to name.
/// </remarks>
public static class CheckDescription
{
    /// <summary>What a box is called when the page gave it neither words nor a description.</summary>
    public const string Unnamed = "Tick box";

    /// <summary>
    /// The description to announce for a box: its name, then its state.
    /// </summary>
    /// <param name="pageDescription">A description the page set on the control, if any.</param>
    /// <param name="text">The label beside the box, if it is plain words.</param>
    /// <param name="isChecked">Whether the box is ticked.</param>
    public static string For(string? pageDescription, string? text, bool isChecked) =>
        $"{Name(pageDescription, text)}, {(isChecked ? "ticked" : "not ticked")}";

    /// <summary>The name half of <see cref="For"/>: the page's description, else the label, else <see cref="Unnamed"/>.</summary>
    public static string Name(string? pageDescription, string? text)
    {
        if (!string.IsNullOrWhiteSpace(pageDescription))
            return pageDescription;
        if (!string.IsNullOrWhiteSpace(text))
            return text;
        return Unnamed;
    }
}
