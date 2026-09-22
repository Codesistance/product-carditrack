using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Controls;
using CardiTrack.Mobile.Core.Api;

namespace CardiTrack.Mobile.Services;

/// <summary>Which contact record the isolated edit form is opened on.</summary>
public enum ContactEditKind
{
    /// <summary>Name and number of whoever the family wants reached first.</summary>
    EmergencyContact,

    /// <summary>The CardiMember's own number — one field, and no name of its own.</summary>
    MemberPhone,
}

/// <summary>
/// What the contact edit form came back with. Null on either field means "not given", which is
/// how a record is cleared — the fields are optional on the API too, so an emergency contact can
/// be removed from the same form that added it.
/// </summary>
public sealed record ContactEdit(string? Name, string? Phone);

/// <summary>Drives the popup's icon glyph and accent colour.</summary>
public enum PopupSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>What the caregiver did with the question <see cref="IPopupService.ShowPendingQuestionAsync"/>
/// opened. <see cref="Cancelled"/> covers every way of leaving without acting — the scrim, the
/// back button — same as a null return from the other popups.</summary>
public enum QuestionPopupOutcome
{
    Cancelled,
    Answered,
    Dismissed,
}

/// <summary>
/// The popup only reports intent; <see cref="Answer"/> is the trimmed text to save. It does not
/// call the API itself — the caller does that, the same ownership <c>QuestionnairesPage</c> and
/// <c>CardiMemberDetailPage</c> already have over their own inline <c>QuestionCard</c>s, so there
/// is exactly one place per screen that decides what an answer or a skip does next.
/// </summary>
public sealed record QuestionPopupResult(QuestionPopupOutcome Outcome, string? Answer = null)
{
    public static readonly QuestionPopupResult Cancelled = new(QuestionPopupOutcome.Cancelled);
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

    /// <summary>
    /// Asks the user to pick one row of a list that already has an answer — what a
    /// <c>ChoiceField</c> opens. The current row is marked so the caregiver sees what is set
    /// before changing it. Returns the tapped row's index, or null when cancelled or dismissed.
    /// </summary>
    /// <remarks>
    /// Not <see cref="ChooseAsync"/>, which answers a question with no current answer and hands
    /// back a label: a field's options can repeat a label legitimately, and its page addresses
    /// them by index, as it did with the Picker.
    /// </remarks>
    Task<int?> ChooseIndexAsync(string title, IReadOnlyList<string> options, int selectedIndex);

    /// <summary>
    /// The Family tab's switcher drawer: the families the caregiver is in, the asks they are
    /// waiting on, and the two ways to another family. Null when they closed it without choosing.
    /// </summary>
    Task<FamilySwitcherChoice?> ChooseFamilyAsync(
        IReadOnlyList<FamilySwitcherRow> families, IReadOnlyList<string> waitingOn);

    /// <summary>
    /// Opens the M1-13 contact carousel's own edit form on one record: the emergency contact, or
    /// the CardiMember's own number. Returns what was entered, or null when cancelled — an
    /// unchanged return is still a return, so the caller compares before it saves.
    /// </summary>
    /// <remarks>
    /// A form scoped to the record rather than a trip to the M1-14 profile page, which is every
    /// field about the member and only two of them the one being fixed. Routed through this
    /// service like the rest, so <see cref="IsShowing"/> covers it and the page underneath knows
    /// it was covered rather than left.
    /// </remarks>
    Task<ContactEdit?> EditContactAsync(ContactEditKind kind, string? name, string? phone);

    /// <summary>
    /// Opens the health background's own edit form. Returns the notes as typed, an empty string
    /// when the caregiver cleared them, or null when they cancelled or dismissed.
    /// </summary>
    /// <remarks>
    /// Three outcomes rather than two, because clearing a background is a real instruction and
    /// must not arrive looking like walking away. The caller compares against what it had before
    /// saving: an unchanged return is still a return.
    /// </remarks>
    Task<string?> EditMedicalNotesAsync(string? firstName, string? notes);

    /// <summary>Shows the detail behind a dashboard/detail weather chip. Completes once dismissed.</summary>
    Task ShowWeatherAsync(WeatherSnapshotResponse weather);

    /// <summary>
    /// Shows what the freshness dot on a CardiMember card means: the tier's own colour, when
    /// data last arrived and the pipeline's word for the state. Completes once dismissed.
    /// </summary>
    /// <param name="tier">The API's freshness word — red, amber, blue or green.</param>
    /// <param name="stateMessage">That tier's description, as the API worded it.</param>
    /// <param name="lastSyncedUtc">When data last arrived, or null if it never has.</param>
    Task ShowSyncStatusAsync(string? tier, string? stateMessage, DateTime? lastSyncedUtc);

    /// <summary>
    /// Asks which stretch of the CardiJournal to export, in the cadence's own unit — days,
    /// whole weeks or whole months. Returns the range as it will be exported, already widened
    /// to whole periods, or null when dismissed.
    /// </summary>
    Task<(DateOnly From, DateOnly To)?> ChooseJournalRangeAsync(JournalCadence cadence, DateOnly today);

    /// <summary>
    /// Asks which file an export should produce. Returns null when dismissed, which every
    /// caller treats as "cancelled".
    /// </summary>
    Task<ReportFormat?> ChooseExportFormatAsync();

    /// <summary>
    /// Asks where a finished export should go, naming the file and — when the platform can keep
    /// one — where Save would put it. Returns null when dismissed, which the flow treats as
    /// "cancelled": the file stays in the cache and the next export sweeps it.
    /// </summary>
    /// <param name="saveHint">Where Save lands here ("Downloads"), or null to hide the Save tile
    /// on a platform that cannot keep a file anywhere the caregiver would find it.</param>
    Task<ExportDelivery?> ChooseExportDeliveryAsync(string fileName, string? saveHint);

    /// <summary>
    /// Opens the CardiMember card's pending question as a modal — the same <c>QuestionCard</c>
    /// <c>QuestionnairesPage</c>/<c>CardiMemberDetailPage</c> show inline, in the popup shell
    /// <see cref="ShowWeatherAsync"/> uses. Completes with what the caregiver did; see
    /// <see cref="QuestionPopupResult"/> for why this doesn't call the API itself.
    /// </summary>
    Task<QuestionPopupResult> ShowPendingQuestionAsync(
        QuestionnaireResponse questionnaire, string? memberFirstName);

    /// <summary>
    /// Asks for the account password. Returns the typed password, or null when
    /// cancelled. The caller verifies it — this popup does not call Auth0.
    /// </summary>
    Task<string?> AskPasswordAsync(string title, string message);
}
