namespace CardiTrack.Mobile.Core.Forms;

/// <summary>
/// What a screen reader is told about one of the app's own value fields — the choice field and
/// the date field — kept out of the controls so the two say it the same way.
/// </summary>
/// <remarks>
/// A field is announced as its label and its value, "Date of birth: 6 Aug 1960", so a caregiver
/// hears what the field is for before what is in it. The label is the page's description of the
/// control when it set one, else whatever the control calls itself; a field with neither is its
/// value alone, which is better than nothing but is a field a page forgot to name.
/// </remarks>
public static class FieldDescription
{
    /// <summary>
    /// The description for a field.
    /// </summary>
    /// <param name="label">What the field is for, or null when nobody said.</param>
    /// <param name="value">What is in it, or null when nothing is.</param>
    /// <param name="unset">How an empty field is described — "not set", "no date chosen".</param>
    public static string For(string? label, string? value, string unset)
    {
        var content = string.IsNullOrWhiteSpace(value) ? unset : value;
        return string.IsNullOrWhiteSpace(label) ? content : $"{label}: {content}";
    }
}
