using CardiTrack.Mobile.Controls;

namespace CardiTrack.Mobile.Services;

public sealed class PopupService : IPopupService
{
    public Task ShowInfoAsync(string message, string? title = null, string? buttonText = null) =>
        ShowAsync(PopupSeverity.Info, title ?? "Just so you know", message, buttonText ?? "Got it", cancelText: null);

    public Task ShowWarningAsync(string message, string? title = null, string? buttonText = null) =>
        ShowAsync(PopupSeverity.Warning, title ?? "Heads up", message, buttonText ?? "Okay", cancelText: null);

    public Task ShowErrorAsync(string message, string? title = null, string? buttonText = null) =>
        ShowAsync(PopupSeverity.Error, title ?? "Something went wrong", message, buttonText ?? "Okay", cancelText: null);

    public Task<bool> ConfirmWarningAsync(string message, string? title = null, string? confirmText = null, string? cancelText = null) =>
        ShowAsync(PopupSeverity.Warning, title ?? "Are you sure?", message, confirmText ?? "Yes, continue", cancelText ?? "Cancel");

    private static Task<bool> ShowAsync(PopupSeverity severity, string title, string message, string confirmText, string? cancelText) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var page = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page is null)
                return false; // No window yet (early startup) — nothing to attach to.

            var popup = new AppPopupPage(severity, title, message, confirmText, cancelText);
            await page.Navigation.PushModalAsync(popup, animated: false);
            return await popup.Result;
        });
}
