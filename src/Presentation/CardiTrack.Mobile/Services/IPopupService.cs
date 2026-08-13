namespace CardiTrack.Mobile.Services;

/// <summary>Drives the popup's icon glyph and accent colour.</summary>
public enum PopupSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// Central app-styled popups replacing the stock <c>DisplayAlertAsync</c> dialogs.
/// Default titles are friendly phrases ("Heads up", "Something went wrong") rather
/// than severity words; pass a title only when a more specific one reads better.
/// </summary>
public interface IPopupService
{
    /// <summary>
    /// Whether one of these popups is currently over the app.
    /// </summary>
    /// <remarks>
    /// A popup is a modal page, so pushing one hides the page underneath and closing one raises
    /// that page's <c>OnAppearing</c> again — indistinguishable, from the page's side, from being
    /// navigated back to. A screen that reloads itself on arrival reads this to tell the two
    /// apart: the caregiver who dismisses an explanation has not gone anywhere, and refetching
    /// under them rebuilds what they were reading. Stays true for the whole of the closing
    /// handshake, so it is still set when that <c>OnAppearing</c> arrives.
    /// </remarks>
    bool IsShowing { get; }

    Task ShowInfoAsync(string message, string? title = null, string? buttonText = null);

    Task ShowWarningAsync(string message, string? title = null, string? buttonText = null);

    Task ShowErrorAsync(string message, string? title = null, string? buttonText = null);

    /// <summary>Warning-styled confirmation; false when cancelled or dismissed via back.</summary>
    Task<bool> ConfirmWarningAsync(string message, string? title = null, string? confirmText = null, string? cancelText = null);

    /// <summary>
    /// Info-styled confirmation — an offer rather than a caution, for the "shall I take you
    /// there?" prompts where nothing is at stake if the user declines. False when cancelled or
    /// dismissed via back.
    /// </summary>
    Task<bool> ConfirmInfoAsync(string message, string? title = null, string? confirmText = null, string? cancelText = null);

    /// <summary>
    /// Asks the user to pick one of several options — used for the M1-13 pause duration.
    /// Returns null when cancelled or dismissed via back.
    /// </summary>
    Task<string?> ChooseAsync(string title, string cancelText, params string[] options);
}
