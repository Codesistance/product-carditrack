using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Controls;

namespace CardiTrack.Mobile.Services;

public sealed class PopupService : IPopupService
{
    /// <summary>
    /// How many of this service's modals are on screen — a count rather than a flag because a
    /// handler that answers one popup with another (a confirmation raising an error) has both on
    /// the stack for the moment in between.
    /// </summary>
    private int _open;

    /// <inheritdoc/>
    public bool IsShowing => Volatile.Read(ref _open) > 0;

    public Task ShowInfoAsync(string message, string? title = null, string? buttonText = null) =>
        ShowAsync(PopupSeverity.Info, title ?? "Just so you know", message, buttonText ?? "Got it", cancelText: null);

    public Task ShowWarningAsync(string message, string? title = null, string? buttonText = null) =>
        ShowAsync(PopupSeverity.Warning, title ?? "Heads up", message, buttonText ?? "Okay", cancelText: null);

    public Task ShowErrorAsync(string message, string? title = null, string? buttonText = null) =>
        ShowAsync(PopupSeverity.Error, title ?? "Something went wrong", message, buttonText ?? "Okay", cancelText: null);

    public Task<bool> ConfirmWarningAsync(string message, string? title = null, string? confirmText = null, string? cancelText = null) =>
        ShowAsync(PopupSeverity.Warning, title ?? "Are you sure?", message, confirmText ?? "Yes, continue", cancelText ?? "Cancel");

    public Task<bool> ConfirmInfoAsync(string message, string? title = null, string? confirmText = null, string? cancelText = null) =>
        ShowAsync(PopupSeverity.Info, title ?? "Just so you know", message, confirmText ?? "Yes, continue", cancelText ?? "Not now");

    /// <summary>
    /// Uses <see cref="AppChooserPage"/> rather than the platform action sheet. The sheet was
    /// correct in structure — a list of choices needs a list, which the two-button
    /// <see cref="AppPopupPage"/> cannot be — but it rendered in system chrome: square corners,
    /// system font, system colours, in an app that is rounded, Quicksand and gradient
    /// everywhere else. AppChooserPage keeps the list and drops the borrowed styling.
    /// </summary>
    public Task<string?> ChooseAsync(string title, string cancelText, params string[] options) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var page = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page is null)
                return null;

            var chooser = new AppChooserPage(title, cancelText, options);
            Interlocked.Increment(ref _open);
            try
            {
                await page.Navigation.PushModalAsync(chooser, animated: false);
                return await chooser.Result;
            }
            finally
            {
                // Released only once the chooser has left the modal stack — the page underneath
                // is raised on the way out, and it reads IsShowing to know that it never left.
                Interlocked.Decrement(ref _open);
            }
        });

    public Task ShowWeatherAsync(WeatherSnapshotResponse weather) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var page = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page is null)
                return;

            var popup = new WeatherPopupPage(weather);
            Interlocked.Increment(ref _open);
            try
            {
                await page.Navigation.PushModalAsync(popup, animated: false);
                await popup.Closed;
            }
            finally
            {
                Interlocked.Decrement(ref _open);
            }
        });

    // Not static: the open count it keeps is this service's own state.
    private Task<bool> ShowAsync(PopupSeverity severity, string title, string message, string confirmText, string? cancelText) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var page = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page is null)
                return false; // No window yet (early startup) — nothing to attach to.

            var popup = new AppPopupPage(severity, title, message, confirmText, cancelText);
            Interlocked.Increment(ref _open);
            try
            {
                await page.Navigation.PushModalAsync(popup, animated: false);
                return await popup.Result;
            }
            finally
            {
                Interlocked.Decrement(ref _open);
            }
        });
}
