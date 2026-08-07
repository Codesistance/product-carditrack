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
    Task ShowInfoAsync(string message, string? title = null, string? buttonText = null);

    Task ShowWarningAsync(string message, string? title = null, string? buttonText = null);

    Task ShowErrorAsync(string message, string? title = null, string? buttonText = null);

    /// <summary>Warning-styled confirmation; false when cancelled or dismissed via back.</summary>
    Task<bool> ConfirmWarningAsync(string message, string? title = null, string? confirmText = null, string? cancelText = null);

    /// <summary>
    /// Asks the user to pick one of several options — used for the M1-13 pause duration.
    /// Returns null when cancelled or dismissed via back.
    /// </summary>
    Task<string?> ChooseAsync(string title, string cancelText, params string[] options);
}
