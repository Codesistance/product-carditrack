using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Controls;
using CardiTrack.Mobile.Core.Alerts;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Auth;
using CardiTrack.Mobile.Core.Forms;
using CardiTrack.Mobile.Core.Members;
using CardiTrack.Mobile.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls.Shapes;

namespace CardiTrack.Mobile;

/// <summary>
/// Answering an alert: acknowledge or close, with a canned response, a note, or both (D-20).
/// </summary>
/// <remarks>
/// <para>
/// The chips are the alert's own <c>responseOptions</c> for this kind of answer. They are never a
/// list held by this client: a code the server does not know for that rule is a 400 naming the
/// valid ones, and two copies of a catalogue is two chances for one to drift.
/// </para>
/// <para>
/// <strong>Offline is refused, not queued.</strong> Offline sync is R4, and a response that
/// silently never landed is worse than one the caregiver knows to send again — they would believe
/// the family had been told.
/// </para>
/// <para>
/// The draft survives the app being backgrounded: the note and the chip are written as they
/// change and read back on the way in, through <see cref="AlertResponseDraftStore"/> — the
/// keystore, under the signed-in account, with writes applied in order and every draft dropped at
/// sign-out. A note is free text about the wearer, which is why the server encrypts it at rest and
/// why a plaintext copy in shared preferences, which is in every device backup, was wrong.
/// </para>
/// </remarks>
[QueryProperty(nameof(AlertId), "alertId")]
[QueryProperty(nameof(Kind), "kind")]
public partial class AlertRespondPage : ContentPage
{
    public const string Route = "alertrespond";

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;
    private readonly AlertResponseDraftStore _drafts;
    private readonly ILogger<AlertRespondPage> _logger;
    private readonly string _scope;

    private readonly AlertResponseDraft _draft = new();
    private readonly Dictionary<string, Border> _chips = [];

    private Guid _alertId;
    private AlertAnswerKind _kind = AlertAnswerKind.Acknowledge;
    private AlertDetailResponse? _alert;
    private bool _busy;
    private bool _loaded;

    public AlertRespondPage(
        ICardiTrackApiClient api,
        IPopupService popups,
        AlertResponseDraftStore drafts,
        IAuthService auth,
        ILogger<AlertRespondPage> logger)
    {
        InitializeComponent();
        _api = api;
        _popups = popups;
        _drafts = drafts;
        _logger = logger;
        // Fixed for the page's life: a page is one caregiver's, and a session that changes under
        // it is a root swap, not a new draft.
        _scope = AlertResponseDraftStore.ScopeFor(auth.CurrentUserEmail);
    }

    public string AlertId
    {
        set => _alertId = Guid.TryParse(Uri.UnescapeDataString(value ?? string.Empty), out var id)
            ? id
            : Guid.Empty;
    }

    public string Kind
    {
        set => _kind = AlertAnswerKinds.Parse(Uri.UnescapeDataString(value ?? string.Empty))
            ?? AlertAnswerKind.Acknowledge;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_loaded)
            return;
        _loaded = true;
        RouteArrival.WhenRouteHasLanded(this, () => _ = LoadAsync());
    }

    /// <summary>
    /// Backgrounding is not leaving: Android may kill the process while the caregiver takes the
    /// call this note is about, and the half-written sentence has to be there when they come back.
    /// </summary>
    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        SaveDraft();
    }

    private void OnRetryClicked(object? sender, EventArgs e) => _ = LoadAsync();

    private async void OnBackTapped(object? sender, TappedEventArgs e)
    {
        SaveDraft();
        await this.GoBackAsync(AppShell.AlertsRoute);
    }

    private async Task LoadAsync()
    {
        if (_alertId == Guid.Empty)
        {
            ErrorDetailLabel.Text = "We couldn't tell which alert this is. Open it again from the list.";
            SetState(error: true);
            return;
        }

        SetState(loading: true);
        HeaderTitle.Text = AlertAnswerKinds.Title(_kind);
        SubmitButton.Text = AlertAnswerKinds.ButtonText(_kind);
        SubmitButton.Icon = _kind == AlertAnswerKind.Close ? "icon_btn_resolve.svg" : "icon_check_white.svg";

        try
        {
            _alert = await _api.GetAlertAsync(_alertId);
            await RestoreDraftAsync();
            Apply(_alert);
            SetState(loaded: true);
        }
        catch (ApiException ex)
        {
            ErrorDetailLabel.Text = ex.Message;
            SetState(error: true);
        }
    }

    private void Apply(AlertDetailResponse alert)
    {
        var first = alert.MemberFirstName();
        HeaderSubtitle.Text = string.IsNullOrWhiteSpace(first) ? alert.Title : $"{first} · {alert.Title}";
        PromptLabel.Text = AlertAnswerKinds.Prompt(_kind);

        // The context card: who, what, when, and how serious, in the alert's own colour.
        var (word, colorKey) = AlertSeverityLook.For(alert.Severity);
        ContextRail.BackgroundColor = MetricStatus.Resource(colorKey, Colors.Gray);
        ContextAvatar.Apply(alert.CardiMemberName, alert.CardiMemberPhotoUrl);
        ContextTitleLabel.Text = alert.Title;
        var when = alert.AboutDate != default
            ? alert.AboutDate.ToString("d MMM")
            : DateTime.SpecifyKind(alert.TriggeredAt, DateTimeKind.Utc).ToLocalTime().ToString("d MMM");
        var severity = char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();
        ContextDetailLabel.Text = string.IsNullOrWhiteSpace(first)
            ? $"{when} · {severity}"
            : $"{first} · {when} · {severity}";

        NoteHintLabel.Text = _kind == AlertAnswerKind.Close
            ? "Whatever you write here is shown to everybody else watching, with your name on it."
            : "Anything here goes to the rest of the family, so nobody duplicates what you're doing.";

        var options = AlertAnswerKinds.OptionsFor(alert, _kind);
        ChipsTitleLabel.Text = _kind == AlertAnswerKind.Close ? "What happened?" : "What are you doing?";
        ChipsHost.Clear();
        _chips.Clear();
        foreach (var option in options)
        {
            var chip = Chip(option);
            _chips[option.Code] = chip;
            ChipsHost.Add(chip);
        }

        NoteEditor.Text = _draft.Note;
        ApplyChipSelection();
        UpdateCounter();
    }

    /// <summary>
    /// One canned response, as a radio row — the pickers' pattern (ChoiceSheetPage): a filled row
    /// with an empty ring, and the chosen one tinted, ringed in Primary and carrying a filled
    /// check. Tapping the chosen row again clears it — a caregiver who picked the wrong one should
    /// not have to send it or leave the page to take it back, and a note alone is a complete
    /// answer.
    /// </summary>
    private Border Chip(AlertResponseOptionResponse option)
    {
        var label = new Label
        {
            Text = option.Label,
            FontFamily = "QuicksandMedium",
            FontSize = 15,
            LineBreakMode = LineBreakMode.WordWrap,
            VerticalOptions = LayoutOptions.Center,
        };

        var row = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
            ColumnSpacing = 12,
            MinimumHeightRequest = 48,
            Padding = new Thickness(14, 8),
        };
        row.Add(label, 0, 0);
        row.Add(new ContentView { VerticalOptions = LayoutOptions.Center }, 1, 0);

        var chip = new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = 6 },
            Content = row,
        };

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            _draft.Code = _draft.Code == option.Code ? null : option.Code;
            ApplyChipSelection();
            SaveDraft();
        };
        chip.GestureRecognizers.Add(tap);
        return chip;
    }

    private void ApplyChipSelection()
    {
        var primary = MetricStatus.Resource("Primary", Colors.Blue);
        foreach (var (code, chip) in _chips)
        {
            var selected = _draft.Code == code;
            chip.BackgroundColor = MetricStatus.Resource(
                selected ? "SelectedOptionBackground" : "InputBackground", Colors.LightGray);
            chip.Stroke = selected ? new SolidColorBrush(primary) : new SolidColorBrush(Colors.Transparent);
            chip.StrokeThickness = selected ? 1.5 : 0;

            if (chip.Content is Grid row && row.Children[0] is Label label && row.Children[1] is ContentView markHost)
            {
                label.TextColor = selected ? primary : MetricStatus.Resource("HeadingText", Colors.Black);
                label.FontFamily = selected ? "QuicksandSemiBold" : "QuicksandMedium";
                markHost.Content = RadioMark(selected, primary);
                SemanticProperties.SetDescription(chip, selected ? $"{label.Text}, chosen" : label.Text);
            }
        }

        SubmitButton.IsEnabled = _draft.CanSubmit;
    }

    /// <summary>An empty ring, or — for the chosen row — a filled check in Primary.</summary>
    private static View RadioMark(bool selected, Color primary) => selected
        ? new Border
        {
            WidthRequest = 20,
            HeightRequest = 20,
            StrokeThickness = 0,
            BackgroundColor = primary,
            StrokeShape = new Ellipse(),
            Content = new Image
            {
                Source = "icon_check_white.svg",
                WidthRequest = 12,
                HeightRequest = 12,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
            },
        }
        : new Border
        {
            WidthRequest = 20,
            HeightRequest = 20,
            Stroke = MetricStatus.Resource("MutedText", Colors.Gray),
            StrokeThickness = 2,
            BackgroundColor = Colors.Transparent,
            StrokeShape = new Ellipse(),
        };

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        SaveDraft();
        await this.GoBackAsync(AppShell.AlertsRoute);
    }

    /// <summary>
    /// Every keystroke is saved, not just the ones followed by leaving the page.
    /// </summary>
    /// <remarks>
    /// Android does not raise <c>OnDisappearing</c> when the app is backgrounded from a Shell
    /// page, so a note written and then interrupted by the phone call it is about was lost —
    /// which is the exact case the draft exists for. Preferences writes are cheap and this string
    /// is capped at 500 characters, so there is nothing to debounce away.
    /// </remarks>
    private void OnNoteChanged(object? sender, TextChangedEventArgs e)
    {
        _draft.Note = e.NewTextValue ?? string.Empty;
        UpdateCounter();
        SubmitButton.IsEnabled = _draft.CanSubmit;
        SaveDraft();
    }

    private void UpdateCounter()
    {
        CounterLabel.Text = _draft.Counter;
        CounterLabel.TextColor = MetricStatus.Resource(
            _draft.Note.Length >= AlertResponseDraft.NoteLimit ? "StatusOrange" : "MutedText", Colors.Gray);
    }

    private async void OnSubmitClicked(object? sender, EventArgs e)
    {
        if (_busy || _alert is not { } alert || !_draft.CanSubmit)
            return;

        _busy = true;
        SubmitButton.IsEnabled = false;
        try
        {
            var answer = _draft.ToRequest();
            var result = _kind == AlertAnswerKind.Close
                ? await _api.CloseAlertAsync(alert.AlertId, answer)
                : await _api.AcknowledgeAlertAsync(alert.AlertId, answer);

            ClearDraft();

            // The count the server reports, not a promise the client makes: it says how many
            // other caregivers were told, which is the whole reason for saying anything.
            await _popups.ShowInfoAsync(
                result.FamilyNotified switch
                {
                    0 => "Saved. Nobody else is watching this person yet, so there was no one to tell.",
                    1 => "Saved, and the other person watching has been told.",
                    var n => $"Saved, and the {n} other people watching have been told.",
                },
                _kind == AlertAnswerKind.Close ? "Resolved" : "Acknowledged");

            await Shell.Current.GoToAsync($"//alerts/{AlertDetailPage.Route}?alertId={alert.AlertId}");
        }
        catch (ApiException ex) when (ex.IsNetworkFailure)
        {
            // Refused, not queued — see the remarks on this class.
            await _popups.ShowWarningAsync(
                "You're offline, so this hasn't been sent. Nothing has been saved — try again when you have a connection.",
                "Not sent");
        }
        catch (ApiException ex)
        {
            await _popups.ShowWarningAsync(ex.Message, "Couldn't save that");
        }
        finally
        {
            _busy = false;
            SubmitButton.IsEnabled = _draft.CanSubmit;
        }
    }

    /// <summary>
    /// Hands the draft to the store, and does not wait.
    /// </summary>
    /// <remarks>
    /// Fire-and-forget on purpose: this runs on every keystroke, and awaiting a keystore write in
    /// a text handler would put its latency between the caregiver and their own typing. The store
    /// applies writes in the order they arrive, so nothing is lost to a race — the last keystroke
    /// is the last write. Failures are recorded rather than shown; a draft is a convenience, and a
    /// popup about it would land while somebody is writing about an alert.
    /// </remarks>
    private void SaveDraft()
    {
        if (_alertId == Guid.Empty)
            return;

        _ = SaveDraftAsync();

        async Task SaveDraftAsync()
        {
            try
            {
                await _drafts.SaveAsync(_scope, _alertId, _kind, _draft);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Saving the alert response draft failed.");
            }
        }
    }

    private async Task RestoreDraftAsync()
    {
        AlertResponseDraft? saved;
        try
        {
            saved = await _drafts.LoadAsync(_scope, _alertId, _kind);
        }
        catch (Exception ex)
        {
            // A keystore that will not open is not worth failing the page for: the caregiver
            // types the note again, which is the state they would have been in anyway.
            _logger.LogDebug(ex, "Reading the alert response draft failed.");
            return;
        }

        if (saved is null)
            return;

        _draft.Code = saved.Code;
        _draft.Note = saved.Note;
    }

    /// <summary>
    /// Drops the draft once it has been sent — the note is now on the alert, where the family
    /// reads it, and a second copy on the device is one the caregiver cannot see to delete.
    /// </summary>
    private void ClearDraft()
    {
        _draft.Code = null;
        _draft.Note = string.Empty;
        SaveDraft();
    }

    private void SetState(bool loading = false, bool loaded = false, bool error = false)
    {
        SkeletonPanel.IsVisible = loading;
        ContentPanel.IsVisible = loaded;
        ErrorPanel.IsVisible = error;
        ActionsPanel.IsVisible = loaded;
    }
}
