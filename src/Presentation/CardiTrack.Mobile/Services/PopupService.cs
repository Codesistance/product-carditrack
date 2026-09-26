using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Controls;
using CardiTrack.Mobile.Core.Alerts;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Domain.Enums;

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

    public Task<int?> ChooseIndexAsync(string title, IReadOnlyList<string> options, int selectedIndex, string? hint = null) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var page = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page is null)
                return null;

            var sheet = new ChoiceSheetPage(title, options, selectedIndex, hint);
            Interlocked.Increment(ref _open);
            try
            {
                await page.Navigation.PushModalAsync(sheet, animated: false);
                return await sheet.Result;
            }
            finally
            {
                // Released only once the sheet has left the modal stack — same handshake as the
                // chooser above, and the page underneath reads IsShowing to know it never left.
                Interlocked.Decrement(ref _open);
            }
        });

    /// <summary>
    /// The one popup in the app that comes up from the bottom rather than sitting in the middle
    /// (see <see cref="FamilySwitcherPage"/>), and the only one that returns something other than
    /// a choice from a list — the drawer's foot offers two actions as well as its rows.
    /// </summary>
    public Task<FamilySwitcherChoice?> ChooseFamilyAsync(
        IReadOnlyList<FamilySwitcherRow> families, IReadOnlyList<string> waitingOn) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var page = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page is null)
                return null;

            var drawer = new FamilySwitcherPage(families, waitingOn);
            Interlocked.Increment(ref _open);
            try
            {
                await page.Navigation.PushModalAsync(drawer, animated: false);
                return await drawer.Result;
            }
            finally
            {
                // Released once the drawer has left the stack, the same handshake the chooser and
                // the choice sheet make — the tab underneath reads IsShowing to know it never left.
                Interlocked.Decrement(ref _open);
            }
        });

    public Task<AlertListFilter?> ChooseAlertFilterAsync(
        AlertListFilter current,
        IReadOnlyList<FilterMember> members,
        bool archived,
        Func<AlertListFilter, CancellationToken, Task<int?>> count) =>
        ShowFilterSheetAsync(() => AlertFilterSheet.Create(current, members, archived, count));

    public Task<JournalFilterChoice?> ChooseJournalFilterAsync(
        JournalFilterChoice current,
        IReadOnlyList<FilterMember> members,
        JournalCadence cadence,
        int pageLimit,
        Func<JournalFilterChoice, CancellationToken, Task<int?>> count) =>
        ShowFilterSheetAsync(() => JournalFilterSheet.Create(current, members, cadence, pageLimit, count));

    /// <summary>
    /// Raises a list's filter sheet and returns its draft if the caregiver asked to see the
    /// results, or null when they dismissed it. Built on the main thread, as every page must be.
    /// </summary>
    private Task<T?> ShowFilterSheetAsync<T>(Func<(FilterSheetPage Page, Func<T> Draft)> create)
        where T : class =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var page = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page is null)
                return null;

            var (sheet, draft) = create();
            Interlocked.Increment(ref _open);
            try
            {
                await page.Navigation.PushModalAsync(sheet, animated: false);
                return await sheet.Result ? draft() : null;
            }
            finally
            {
                // Released once the sheet has left the stack — the same handshake as the drawer
                // above; the list underneath reads IsShowing to know it never left.
                Interlocked.Decrement(ref _open);
            }
        });

    public Task<ContactEdit?> EditContactAsync(ContactEditKind kind, string? name, string? phone) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var page = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page is null)
                return null;

            var form = new ContactEditPopupPage(kind, name, phone);
            Interlocked.Increment(ref _open);
            try
            {
                await page.Navigation.PushModalAsync(form, animated: false);
                return await form.Result;
            }
            finally
            {
                // Released only once the form has left the modal stack — same handshake as the
                // chooser above, and the page underneath reads IsShowing to know it never left.
                Interlocked.Decrement(ref _open);
            }
        });

    public Task<(MedicalEntryKind Kind, string Text)?> EditMedicalEntryAsync(
        string? firstName, MedicalEntryKind kind, string? text) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var page = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page is null)
                return ((MedicalEntryKind, string)?)null;

            var form = new MedicalEntryEditPopupPage(firstName, kind, text);
            Interlocked.Increment(ref _open);
            try
            {
                await page.Navigation.PushModalAsync(form, animated: false);
                return await form.Result;
            }
            finally
            {
                // Released only once the form has left the modal stack — same handshake as the
                // contact form above, and the page underneath reads IsShowing to know it never left.
                Interlocked.Decrement(ref _open);
            }
        });

    public Task<IReadOnlyList<(MedicalEntryKind Kind, string Text)>?> SortMedicalNotesAsync(
        IReadOnlyList<string> statements) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var page = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page is null)
                return (IReadOnlyList<(MedicalEntryKind, string)>?)null;

            var form = new MedicalSortPopupPage(statements);
            Interlocked.Increment(ref _open);
            try
            {
                await page.Navigation.PushModalAsync(form, animated: false);
                return await form.Result;
            }
            finally
            {
                // Same handshake as the forms above.
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

    public Task<bool> ShowNoDeviceAsync(string firstName) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var page = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page is null)
                return false;

            var popup = new NoDevicePopupPage(firstName);
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

    public Task ShowSyncStatusAsync(string? tier, string? stateMessage, DateTime? lastSyncedUtc) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var page = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page is null)
                return;

            var popup = new SyncStatusPopupPage(tier, stateMessage, lastSyncedUtc);
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

    public Task<(DateOnly From, DateOnly To)?> ChooseJournalRangeAsync(
        JournalCadence cadence, DateOnly today) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var page = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page is null)
                return ((DateOnly From, DateOnly To)?)null;

            var popup = new JournalRangePopupPage(cadence, today);
            Interlocked.Increment(ref _open);
            try
            {
                await page.Navigation.PushModalAsync(popup, animated: false);
                return await popup.Closed;
            }
            finally
            {
                Interlocked.Decrement(ref _open);
            }
        });

    public Task<ReportFormat?> ChooseExportFormatAsync() =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var page = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page is null)
                return (ReportFormat?)null;

            var popup = new ExportFormatPopupPage();
            Interlocked.Increment(ref _open);
            try
            {
                await page.Navigation.PushModalAsync(popup, animated: false);
                return await popup.Closed;
            }
            finally
            {
                Interlocked.Decrement(ref _open);
            }
        });

    public Task<ExportDelivery?> ChooseExportDeliveryAsync(string fileName, string? saveHint) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var page = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page is null)
                return (ExportDelivery?)null;

            var popup = new ExportDeliveryPopupPage(fileName, saveHint);
            Interlocked.Increment(ref _open);
            try
            {
                await page.Navigation.PushModalAsync(popup, animated: false);
                return await popup.Closed;
            }
            finally
            {
                Interlocked.Decrement(ref _open);
            }
        });

    public Task<string?> AskPasswordAsync(string title, string message) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var page = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page is null)
                return null;

            var prompt = new AppPasswordPage(title, message);
            Interlocked.Increment(ref _open);
            try
            {
                await page.Navigation.PushModalAsync(prompt, animated: false);
                return await prompt.Result;
            }
            finally
            {
                Interlocked.Decrement(ref _open);
            }
        });

    public Task<QuestionPopupResult> ShowPendingQuestionAsync(
        QuestionnaireResponse questionnaire, string? memberFirstName) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var page = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page is null)
                return QuestionPopupResult.Cancelled;

            // Passes itself for the popup's own "Skip this question?" confirm — nesting one more
            // modal on top of this one, the same as any other confirm shown while a popup is open.
            var popup = new QuestionPopupPage(questionnaire, memberFirstName, this);
            Interlocked.Increment(ref _open);
            try
            {
                await page.Navigation.PushModalAsync(popup, animated: false);
                return await popup.Closed;
            }
            finally
            {
                Interlocked.Decrement(ref _open);
            }
        });

    public Task<bool?> AskInfoAsync(string message, string title, string confirmText, string cancelText) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var page = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page is null)
                return (bool?)null; // No window yet (early startup) — nothing to attach to.

            var popup = new AppPopupPage(PopupSeverity.Info, title, message, confirmText, cancelText);
            Interlocked.Increment(ref _open);
            try
            {
                await page.Navigation.PushModalAsync(popup, animated: false);
                var confirmed = await popup.Result;
                return popup.ClosedByButton ? confirmed : (bool?)null;
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
