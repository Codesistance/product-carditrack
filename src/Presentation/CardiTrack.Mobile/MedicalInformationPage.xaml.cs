using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Controls;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Forms;
using CardiTrack.Mobile.Core.Members;
using CardiTrack.Mobile.Core.Navigation;
using CardiTrack.Mobile.Core.Offline;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

/// <summary>
/// The medical information kept for one CardiMember, as a ledger: one line per condition, allergy
/// or medication, each dated and signed, and a history of the lines that were changed or removed.
/// </summary>
/// <remarks>
/// A page rather than the drop down this used to be on Member Detail, so it reads as a peer of
/// Questions &amp; Answers: both are places the family keeps something, and both can run long
/// enough that opening them in place pushed the rest of the screen out of reach.
/// <para>
/// Lines rather than the one block of text this began as, so each can be confirmed, changed or
/// taken off on its own, and nothing a caregiver changes is lost — the old wording goes to the
/// history. The member's single note is still kept, by the server, as a summary of the lines.
/// </para>
/// <para>
/// Not a live screen — nothing here changes unless somebody edits it — so it refetches when opened
/// and on a pull, and does not poll.
/// </para>
/// </remarks>
[QueryProperty(nameof(MemberId), "memberId")]
[QueryProperty(nameof(EditOnArrival), EditOnArrivalQuery)]
public partial class MedicalInformationPage : ContentPage
{
    /// <summary>Shell route; see <see cref="AppShell"/>.</summary>
    public const string Route = "medicalinformation";

    /// <summary>
    /// Query key that opens the add form as soon as the page has loaded — what the
    /// <c>#medicalNotes</c> deep link sets, so a caregiver who tapped "Add notes" on a
    /// notification lands on somewhere to type rather than on a screen with another button.
    /// </summary>
    public const string EditOnArrivalQuery = "edit";

    // The row actions, in the order the sheet offers them.
    private const string StillAccurate = "Still accurate";
    private const string Change = "Change";
    private const string Remove = "Remove";
    private const string DeletePermanently = "Delete permanently";

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;

    private readonly MemberRoute _route = new();
    private CardiMemberDetailResponse? _member;
    private MedicalEntriesResponse? _ledger;

    private readonly LoadGate _gate = new();
    private readonly RefreshFeedback _feedback;

    /// <summary>Set by the deep link; consumed once the first load lands.</summary>
    private bool _editOnArrival;
    private bool _isSaving;
    private bool _historyOpen;

    /// <summary>The block of old notes the "Sort into lines" prompt is offering to sort, if any.</summary>
    private MedicalEntryResponse? _unsorted;

    public MedicalInformationPage(ICardiTrackApiClient api, IPopupService popups)
    {
        InitializeComponent();
        _api = api;
        _popups = popups;
        _feedback = new RefreshFeedback(SavedBanner, Updating);
    }

    /// <summary>
    /// Whether to open the add form on arrival. A string rather than a bool because Shell hands
    /// query values over as text; anything but "true" is read as no.
    /// </summary>
    public string EditOnArrival
    {
        set => _editOnArrival = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whose medical information this is. Shell may set this after the page has already
    /// appeared and tried to load, so an arrival that leaves a load owed runs it — see
    /// <see cref="MemberRoute"/>.
    /// </summary>
    public string MemberId
    {
        set
        {
            if (_route.Accept(value))
                this.WhenRouteHasLanded(() => _ = LoadAsync(force: true));
        }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = LoadAsync();
    }

    private bool CanEdit => _member?.IsPrimaryCaregiver == true;

    private string FirstName => _member?.DisplayFirstName() ?? string.Empty;

    private async void OnBackTapped(object? sender, EventArgs e) =>
        await this.GoBackAsync($"{AppShell.DashboardRoute}/{CardiMemberDetailPage.Route}?memberId={_route.Id}");

    private void OnRetryClicked(object? sender, EventArgs e) => _ = LoadAsync(force: true);

    private async void OnPullToRefresh(object? sender, EventArgs e)
    {
        await LoadAsync(force: true);
        Refresher.IsRefreshing = false;
    }

    private async void OnAddClicked(object? sender, EventArgs e) => await AddAsync();

    private async void OnAllAccurateClicked(object? sender, EventArgs e) =>
        await WriteAsync(async id =>
        {
            // The member endpoint confirms every current line at once; the ledger is read back
            // so the dates on screen are the ones on file.
            await _api.ConfirmMedicalNotesAsync(id);
            return await _api.GetMedicalEntriesAsync(id);
        }, "Couldn't confirm these");

    private async void OnSortClicked(object? sender, EventArgs e) => await SortAsync();

    /// <summary>
    /// Sorts the block of old notes into lines: each part filed as what the caregiver says it is,
    /// then the block itself taken off — into the history, where it stays as it was written.
    /// </summary>
    /// <remarks>
    /// The new lines go on first and the block comes off last, so a failure part-way leaves the
    /// block where it was rather than half the notes nowhere current. The one exception is a block
    /// too long for the list to hold alongside its own lines — the server caps the whole list at
    /// the single note's 2,000 characters — which comes off first; the history still has every
    /// word of it. Both are measured before anything is written (see the check in the body).
    /// </remarks>
    private async Task SortAsync()
    {
        if (_unsorted is not { } block || !CanEdit || _isSaving)
            return;

        var lines = await _popups.SortMedicalNotesAsync(MedicalLedgerLines.SplitIntoStatements(block.Text));
        if (lines is null)
            return;

        // Refused up front rather than part-way: the server takes one line at a time, and a part
        // over its cap would stop the sort with some lines filed and the block still current.
        if (lines.FirstOrDefault(l => l.Text.Length > MedicalEntryEditPopupPage.MaxLength) is { Text: { } tooLong })
        {
            await _popups.ShowWarningAsync(
                $"\"{Shorten(tooLong)}\" is longer than one line can be. Add it by hand in shorter lines, then remove the block.",
                "One part is too long");
            return;
        }

        // Measured with the server's own composer, so the check is the cap itself rather than a
        // guess at it. Where the list ends up has to fit, or nothing is written: refusing after
        // the first line is filed would leave the sort half done. Whether the block can stay on
        // while the lines go on decides the order — removing it first is always safe once the
        // end state fits, since every step on the way holds a subset of it.
        var others = _ledger!.Current.Where(e => e.Id != block.Id).Select(e => (e.Kind, e.Text)).ToList();
        var sorted = lines.Select(l => (l.Kind, l.Text)).ToList();
        if (ComposedLength([.. others, .. sorted]) > MedicalLedger.MaxSummaryLength)
        {
            await _popups.ShowWarningAsync(
                "Sorted into lines, these notes would be longer than the medical information can hold. Shorten or skip some parts, or remove a line first.",
                "Too long to sort");
            return;
        }

        var tooLongForBoth = ComposedLength([.. others, (block.Kind, block.Text), .. sorted]) > MedicalLedger.MaxSummaryLength;
        await WriteAsync(async id =>
        {
            MedicalEntriesResponse ledger = _ledger!;
            if (tooLongForBoth)
                ledger = await _api.RemoveMedicalEntryAsync(id, block.Id);

            foreach (var (kind, text) in lines)
                ledger = await _api.AddMedicalEntryAsync(id, new MedicalEntryRequest { Kind = kind, Text = text });

            if (!tooLongForBoth)
                ledger = await _api.RemoveMedicalEntryAsync(id, block.Id);
            return ledger;
        }, "Couldn't sort these notes");
    }

    private static int ComposedLength(IEnumerable<(MedicalEntryKind Kind, string Text)> lines) =>
        MedicalLedger.Compose(lines)?.Length ?? 0;

    private void OnHistoryToggled(object? sender, TappedEventArgs e)
    {
        _historyOpen = !_historyOpen;
        ShowHistoryOpen();
    }

    private async Task AddAsync()
    {
        if (!CanEdit || _isSaving)
            return;

        var line = await _popups.EditMedicalEntryAsync(FirstName, MedicalEntryKind.Condition, text: null);
        if (line is not { } saved)
            return;

        await WriteAsync(
            id => _api.AddMedicalEntryAsync(id, new MedicalEntryRequest { Kind = saved.Kind, Text = saved.Text }),
            "Couldn't add this");
    }

    /// <summary>What can be done with a current line, offered on a tap.</summary>
    private async Task OnLineTappedAsync(MedicalEntryResponse line)
    {
        if (!CanEdit || _isSaving)
            return;

        // Only Delete permanently in red. Remove sends the line to the history, where it can still
        // be read, so it is an ordinary choice — red on both said they weighed the same.
        var choice = await _popups.ChooseAsync(
            Shorten(line.Text), "Cancel", [StillAccurate, Change, Remove, DeletePermanently], danger: [DeletePermanently]);

        switch (choice)
        {
            case StillAccurate:
                await WriteAsync(id => _api.ConfirmMedicalEntryAsync(id, line.Id), "Couldn't confirm this");
                break;

            case Change:
                var edited = await _popups.EditMedicalEntryAsync(FirstName, line.Kind, line.Text);
                if (edited is { } saved)
                {
                    await WriteAsync(
                        id => _api.ReviseMedicalEntryAsync(
                            id, line.Id, new MedicalEntryRequest { Kind = saved.Kind, Text = saved.Text }),
                        "Couldn't save this");
                }
                break;

            // No confirmation: nothing is lost — the line goes to the history, and the history is
            // one tap below.
            case Remove:
                await WriteAsync(id => _api.RemoveMedicalEntryAsync(id, line.Id), "Couldn't remove this");
                break;

            case DeletePermanently:
                await EraseAsync(line);
                break;
        }
    }

    /// <summary>A line in the history can only be deleted for good; nothing else about it changes.</summary>
    private async Task OnHistoryLineTappedAsync(MedicalEntryResponse line)
    {
        if (!CanEdit || _isSaving)
            return;

        if (await _popups.ChooseAsync(Shorten(line.Text), "Cancel", DeletePermanently) == DeletePermanently)
            await EraseAsync(line);
    }

    /// <summary>
    /// The one change that cannot be undone, so the only one asked twice. Removing keeps the line
    /// in the history; this takes it out of the record altogether.
    /// </summary>
    private async Task EraseAsync(MedicalEntryResponse line)
    {
        var sure = await _popups.ConfirmWarningAsync(
            "This line will be gone from the record and its history for everyone. Remove keeps it in the history instead.",
            "Delete permanently?",
            confirmText: "Delete",
            cancelText: "Keep it");
        if (sure)
            await WriteAsync(id => _api.EraseMedicalEntryAsync(id, line.Id), "Couldn't delete this");
    }

    /// <summary>
    /// Runs a ledger write and repaints from the ledger the server answers with, so every date on
    /// screen is the one on file rather than one this screen guessed at.
    /// </summary>
    private async Task WriteAsync(Func<Guid, Task<MedicalEntriesResponse>> write, string errorTitle)
    {
        if (_member is null || _isSaving)
            return;

        _isSaving = true;
        try
        {
            ShowLedger(await write(_member.Id));
        }
        catch (ApiException ex) when (!ex.IsSessionExpired)
        {
            await _popups.ShowErrorAsync(
                ex.Errors is { Count: > 0 } ? string.Join('\n', ex.Errors) : ex.Message, errorTitle);
        }
        catch (ApiException)
        {
            // Session gone — the app is already on its way back to sign-in.
        }
        finally
        {
            _isSaving = false;
        }
    }

    /// <param name="force">
    /// Supersedes a load already in flight rather than skipping — for anything the caregiver
    /// asked for by hand. A gesture that did nothing because a slow request happened to be
    /// running is a gesture they will make again.
    /// </param>
    private async Task LoadAsync(bool force = false)
    {
        if (_gate.IsLoading && !force)
            return;
        // Nothing to ask about yet. The empty id is not a member the API can refuse politely —
        // every CardiMember endpoint answers it with "CardiMember not found", which reads as
        // this member being gone — so the request is not made at all, and the arrival that
        // brings the id runs this again.
        if (_route.IsMissing)
        {
            _route.LoadedWithoutId();
            ErrorDetailLabel.Text = MemberRoute.MissingMessage;
            SetState(error: true);
            return;
        }

        var ticket = _gate.Begin();
        var memberId = _route.Id;

        if (_member is null)
            SetState(loading: true);

        try
        {
            // The saved profile first on a landing with nothing on screen, the live one behind
            // it — for the name, and for whether this caregiver may change anything. The ledger
            // itself is always read live, below.
            var outcome = await SnapshotRefresh.RunAsync(
                _api, _gate, ticket,
                peek: _member is null ? ct => _api.PeekCardiMemberAsync(memberId, ct) : null,
                fetch: ct => _api.GetCardiMemberAsync(memberId, ct),
                render: member =>
                {
                    _member = member;
                    ApplyMember(member);
                    SetState(loaded: true);
                },
                _feedback,
                sameAs: (a, b) => SamePayload.Same(
                    new { a.Name, a.MedicalNotes, a.IsPrimaryCaregiver, a.MedicalNotesReviewedAtUtc },
                    new { b.Name, b.MedicalNotes, b.IsPrimaryCaregiver, b.MedicalNotesReviewedAtUtc }));

            if (outcome.Result == RefreshResult.NothingAndFailed)
            {
                _member = null;
                ErrorDetailLabel.Text = outcome.Error!.Message;
                SetState(error: true);
                return;
            }

            await LoadLedgerAsync(memberId);
        }
        catch (Exception ex)
        {
            // Anything that is not the API answering badly — a fault while putting the record on
            // screen. Without this it is silent and permanent: the callers are an async void pull
            // handler and a fire-and-forget OnAppearing, so nothing observes the throw.
            ScreenRefresh.LogFailure(ex, this, "while loading");
            if (_member is null)
            {
                ErrorDetailLabel.Text = "Something went wrong while showing this.";
                SetState(error: true);
            }
        }
        finally
        {
            _gate.Release(ticket);
        }
    }

    /// <summary>
    /// The lines themselves. A failure here keeps the page: the member's own summary of the notes
    /// is already on the profile, so the record is shown as that one block with a way to try again,
    /// rather than an error screen over information the caregiver may need right now.
    /// </summary>
    private async Task LoadLedgerAsync(Guid memberId)
    {
        try
        {
            ShowLedger(await _api.GetMedicalEntriesAsync(memberId));
        }
        catch (ApiException ex) when (!ex.IsSessionExpired && _ledger is null)
        {
            ShowSummaryFallback();
        }
        catch (ApiException)
        {
            // A failed refresh keeps the lines already on screen; an expired session is already
            // on its way back to sign-in.
        }

        // The deep link's request, honoured once rather than on every repaint — and only by opening
        // the add form on an empty record. The same link arrives from the "notes are out of date"
        // reminder, and there the thing to do is read what is on file and confirm or change it,
        // which this screen already puts in front of them; a blank form would be the wrong ask.
        if (_editOnArrival && _ledger is not null)
        {
            _editOnArrival = false;
            if (_ledger.Current.Count == 0)
                _ = AddAsync();
        }
    }

    private void ApplyMember(CardiMemberDetailResponse member)
    {
        ChatBot.MemberId = _route.Id;
        ChatBot.MemberFirstName = member.DisplayFirstName();
        AddButton.IsVisible = member.IsPrimaryCaregiver;
    }

    private void ShowLedger(MedicalEntriesResponse ledger)
    {
        _ledger = ledger;
        LedgerList.Clear();

        foreach (var (kind, lines) in MedicalLedgerLines.Group(ledger.Current))
        {
            LedgerList.Add(GroupHeading(kind));

            // Allergies sit on a tint of their own, the one group a caregiver must not miss: what
            // somebody cannot be given matters more than anything else on the card.
            var alert = kind == MedicalEntryKind.Allergy;
            var group = new VerticalStackLayout();
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                group.Add(Row(line, historic: false, ruled: i > 0, alert, () => _ = OnLineTappedAsync(line)));
            }
            LedgerList.Add(alert ? AllergyBand(group) : group);
        }

        var hasLines = ledger.Current.Count > 0;
        EmptyLabel.IsVisible = !hasLines;
        EmptyLabel.Text =
            $"Nothing recorded for {FirstName} yet. Conditions, allergies, medications — anything a "
            + "caregiver should know before they arrive — each go on a line of their own.";

        ShowStatus(ledger);
        _unsorted = CanEdit ? ledger.Current.FirstOrDefault(MedicalLedgerLines.IsUnsortedBlock) : null;
        SortPrompt.IsVisible = _unsorted is not null;

        HistoryList.Clear();
        for (var i = 0; i < ledger.History.Count; i++)
        {
            var line = ledger.History[i];
            HistoryList.Add(Row(line, historic: true, ruled: i > 0, alert: false, () => _ = OnHistoryLineTappedAsync(line)));
        }
        HistoryCard.IsVisible = ledger.History.Count > 0;
        HistoryTitleLabel.Text = $"History ({ledger.History.Count})";
        ShowHistoryOpen();
    }

    /// <summary>
    /// The chip at the head of the list, and the one-tap confirmation beside it — offered only while
    /// the chip is not green. Confirming a list somebody checked two days ago adds nothing; the
    /// per-line "Still accurate" stays in every line's sheet for anybody who wants it anyway.
    /// </summary>
    private void ShowStatus(MedicalEntriesResponse ledger)
    {
        var status = MedicalLedgerLines.ReviewStatus(
            ledger.Current.Count, ledger.ReviewedAtUtc, DateTime.UtcNow);
        StatusRow.IsVisible = status is not null;
        if (status is not { } s)
            return;

        var (tint, ink) = s.Tone switch
        {
            LedgerReviewTone.Current => ("PillGreenBackground", "StatusGreen"),
            LedgerReviewTone.Due => ("PillYellowBackground", "ActionAmberInk"),
            _ => ("PillRedBackground", "DangerRed"),
        };
        StatusChip.BackgroundColor = Resource<Color>(tint);
        StatusLabel.TextColor = Resource<Color>(ink);
        StatusLabel.Text = s.Text;
        AllAccurateButton.IsVisible = CanEdit && s.Tone != LedgerReviewTone.Current;
    }

    /// <summary>The ledger could not be read: the member's own one-block summary, as before the ledger.</summary>
    private void ShowSummaryFallback()
    {
        LedgerList.Clear();
        HistoryCard.IsVisible = false;
        StatusRow.IsVisible = false;
        SortPrompt.IsVisible = false;

        var notes = _member?.MedicalNotes;
        EmptyLabel.IsVisible = true;
        EmptyLabel.Text = string.IsNullOrWhiteSpace(notes)
            ? "We couldn't load this just now. Pull down to try again."
            : $"{notes}\n\nWe couldn't load the separate lines just now. Pull down to try again.";
    }

    private void ShowHistoryOpen()
    {
        HistoryList.IsVisible = _historyOpen;
        HistoryChevron.Rotation = _historyOpen ? 180 : 0;
    }

    /// <summary>A group's heading: its icon, then its name in small capitals — red for allergies.</summary>
    private static View GroupHeading(MedicalEntryKind kind)
    {
        var alert = kind == MedicalEntryKind.Allergy;
        var heading = new HorizontalStackLayout
        {
            Spacing = 6,
            Margin = new Thickness(0, 14, 0, 4),
            Children =
            {
                new Image
                {
                    Source = MedicalLedgerLines.HeadingIcon(kind),
                    WidthRequest = 16,
                    HeightRequest = 16,
                    VerticalOptions = LayoutOptions.Center,
                },
                new Label
                {
                    Text = MedicalLedgerLines.Heading(kind).ToUpperInvariant(),
                    Style = Resource<Style>("Caption"),
                    FontFamily = "QuicksandSemiBold",
                    CharacterSpacing = 1,
                    TextColor = alert ? Resource<Color>("DangerRed") : Resource<Color>("MutedText"),
                    VerticalOptions = LayoutOptions.Center,
                },
            },
        };
        SemanticProperties.SetHeadingLevel(heading, SemanticHeadingLevel.Level3);
        return heading;
    }

    /// <summary>The allergies group on its red tint, rounded like the other in-card panels.</summary>
    private static View AllergyBand(View rows) =>
        new Border
        {
            BackgroundColor = Resource<Color>("PillRedBackground"),
            StrokeThickness = 0,
            Padding = new Thickness(12, 0),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
            Content = rows,
        };

    /// <summary>
    /// One ruled line of the ledger: the words, and under them who put it on file and when — the
    /// one date, rather than a column beside it saying the same again — with a ⋮ at the end for
    /// the caregiver who can act on it, so a tappable row looks like one. A hairline above every
    /// row but the first of its group gives the card its ledger look.
    /// </summary>
    private View Row(MedicalEntryResponse line, bool historic, bool ruled, bool alert, Action tapped)
    {
        var text = new Label
        {
            Text = line.Text,
            Style = Resource<Style>("Body2"),
            FontFamily = alert ? "QuicksandSemiBold" : null,
            TextColor = Resource<Color>(historic ? "MutedText" : alert ? "DangerRed" : "HeadingText"),
            TextDecorations = historic ? TextDecorations.Strikethrough : TextDecorations.None,
        };
        var caption = new Label
        {
            Text = historic ? MedicalLedgerLines.HistoryCaption(line) : MedicalLedgerLines.Caption(line),
            Style = Resource<Style>("Caption"),
        };
        var words = new VerticalStackLayout { Spacing = 2, Children = { text, caption } };
        if (historic)
        {
            // A line from the history says what it was filed under, since it no longer sits
            // under a heading of its own.
            words.Children.Insert(0, new Label
            {
                Text = MedicalLedgerLines.KindName(line.Kind).ToUpperInvariant(),
                Style = Resource<Style>("Caption"),
                FontSize = 10,
                CharacterSpacing = 1,
            });
        }

        var grid = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
            ColumnSpacing = 8,
            Padding = new Thickness(0, 10),
        };
        grid.Add(words, 0);
        if (CanEdit)
        {
            grid.Add(new Image
            {
                Source = "icon_more_vertical.svg",
                WidthRequest = 20,
                HeightRequest = 20,
                VerticalOptions = LayoutOptions.Center,
            }, 1);
        }

        var row = new VerticalStackLayout();
        if (ruled)
            row.Add(new BoxView { HeightRequest = 1, Color = Resource<Color>(alert ? "PillRedBackground" : "Divider") });
        row.Add(grid);

        if (CanEdit)
        {
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => tapped();
            row.GestureRecognizers.Add(tap);
            SemanticProperties.SetHint(row, historic ? "Offers to delete this for good" : "Shows what you can do with this line");
        }
        SemanticProperties.SetDescription(row, $"{line.Text}. {caption.Text}");
        return row;
    }

    private static T Resource<T>(string key) =>
        Microsoft.Maui.Controls.Application.Current!.Resources.TryGetValue(key, out var value) && value is T t
            ? t
            : default!;

    /// <summary>A line as a sheet's title: long ones cut short, since the sheet is about what to do with it.</summary>
    private static string Shorten(string text) => text.Length <= 60 ? text : $"{text[..57].TrimEnd()}…";

    private void SetState(bool loading = false, bool loaded = false, bool error = false)
    {
        SkeletonPanel.IsVisible = loading;
        ContentPanel.IsVisible = loaded;
        ErrorPanel.IsVisible = error;
        Refresher.IsVisible = loaded;
    }
}
