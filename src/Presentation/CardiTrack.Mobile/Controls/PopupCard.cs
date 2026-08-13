namespace CardiTrack.Mobile.Controls;

/// <summary>
/// Sizes the card that <see cref="AppPopupPage"/> and <see cref="AppChooserPage"/> both draw
/// themselves in, so the two shells stay one object at one width.
/// </summary>
/// <remarks>
/// <para>
/// The width is set outright rather than capped with <c>MaximumWidthRequest</c>, which is what the
/// cards used to do and is why the message on a long explanation lost its last lines. A maximum is
/// applied to the <em>result</em> of a measure, not to the width the content is measured at: the
/// card was measured at the full width its margin left it (~412dp on a phone), the message label
/// reported the height the lines it needed <em>there</em> would take, and then the card was
/// arranged at 360 — where the same text wraps into two or three more lines than were measured for,
/// which the Border then clipped. An explicit width is passed down as the measure constraint
/// (<c>ViewHandlerExtensions.MeasureVirtualView</c>), so the label wraps at the width it is
/// ultimately drawn at and asks for the height it actually needs.
/// </para>
/// <para>
/// Read off the page rather than the display, so a resized desktop window or a split-screen phone
/// gets the width it is actually showing rather than the one the device was born with.
/// </para>
/// </remarks>
internal static class PopupCard
{
    /// <summary>
    /// Widest the card is allowed to get. A dialog that grows with a tablet is a dialog nobody can
    /// read a line of: past this the text is wider than the eye tracks comfortably.
    /// </summary>
    private const double MaxWidth = 360;

    /// <summary>
    /// Gap kept between the card and each screen edge on a display too narrow for
    /// <see cref="MaxWidth"/>. Mirrors the margin the card carries in XAML, which is what holds
    /// the gap until the first size pass arrives.
    /// </summary>
    private const double SideGap = 32;

    /// <summary>
    /// Fits <paramref name="card"/> to a page of <paramref name="pageWidth"/>. Safe to call on
    /// every size pass — a page that has not been measured yet (width -1) is left alone.
    /// </summary>
    /// <remarks>
    /// Clamped at zero as well as at <see cref="MaxWidth"/>: a window narrower than the two gaps
    /// would otherwise ask for a negative width, which is not a size MAUI defines behaviour for.
    /// No phone is that narrow, but a desktop window dragged small is.
    /// </remarks>
    public static void Fit(Border card, double pageWidth)
    {
        if (pageWidth <= 0)
            return;

        card.WidthRequest = Math.Clamp(pageWidth - (SideGap * 2), 0, MaxWidth);
    }
}
