using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Controls;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Onboarding;
using Microsoft.Maui.Controls.Shapes;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

/// <summary>
/// The Summaries tab: the member's daybook entries, newest first, one per finished day — searchable
/// over the whole history and narrowable by urgency and by how far back to look. A card opens the
/// review's own page (<see cref="DaybookEntryPage"/>), which carries the full account and the
/// charts to read it against.
/// </summary>
/// <remarks>
/// <para>
/// Took the Family tab's slot. That page was a placeholder for family invitations, which are R3
/// work — a permanent quarter of the bottom navigation spent on a card that said "coming soon",
/// while the daybook entries had no surface at all. When family sharing does land it belongs under
/// Settings or scoped to a member, not back in the bar.
/// </para>
/// <para>
/// Every filter is applied server-side, before the page cap — a caregiver searching "oxygen" is
/// asking about their history, not about whichever page happened to load. The search is debounced
/// the way the questionnaires archive's is, so a caregiver typing "breathing" costs one request,
/// not nine.
/// </para>
/// <para>
/// Refreshes on resume but does not poll. Every other live surface in the app carries
/// <c>RefreshEvery(PeriodicRefresh.LiveDataInterval)</c> because what it shows can change within
/// the minute; a daybook entry is written once, at 02:00 in the member's own local time, and cannot
/// change afterwards. Polling it would be a request every thirty seconds for a list that moves
/// once a day.
/// </para>
/// </remarks>
[QueryProperty(nameof(PreselectMemberId), "memberId")]
public partial class DaybookPage : ContentPage
{
    /// <summary>
    /// How many reviews one load asks for. A month is a page a caregiver actually scrolls; the
    /// service clamps anything larger anyway, and search narrows the history server-side rather
    /// than needing a bigger page.
    /// </summary>
    private const int HistoryLimit = 31;

    /// <summary>Lines of the review shown on the card before it is opened.</summary>
    private const int PreviewLines = 3;

    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(350);

    /// <summary>The window chooser's vocabulary, and what each choice means in days back.</summary>
    private static readonly (string Label, int? Days)[] Windows =
    [
        ("All time", null),
        ("Last 7 days", 7),
        ("Last 30 days", 30),
        ("Last 90 days", 90),
    ];

    private static readonly string[] UrgencyChoices =
        ["Any urgency", "Watch", "Check in", "Concerning", "Act now"];

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;

    private bool _isLoading;
    private bool _returningFromPopup;
    private DateTime _lastLoadedUtc = DateTime.MinValue;
    private bool _hasLoadedOnce;
    private bool _hasAnyReviews;
    private CancellationTokenSource? _searchDebounceCts;

    private IReadOnlyList<CardiMemberResponse> _members = [];
    private Guid? _pendingMemberId;
    private Guid _memberId;
    private string? _memberFirstName;
    private string? _search;
    private string? _urgency;
    private int? _windowDays;

    /// <summary>
    /// A member to land filtered to, passed by the dashboard's member card and the member detail
    /// page — "show me their daybook" should arrive already about them. Consumed on the next
    /// load; an id not on the account (a stale deep link) falls through to the normal
    /// primary-member rule rather than showing an empty page about nobody.
    /// </summary>
    public string PreselectMemberId
    {
        set => _pendingMemberId =
            Guid.TryParse(Uri.UnescapeDataString(value ?? string.Empty), out var id) && id != Guid.Empty
                ? id
                : null;
    }

    public DaybookPage(ICardiTrackApiClient api, IPopupService popups)
    {
        InitializeComponent();
        _api = api;
        _popups = popups;
        this.RefreshWhenAppResumes(RefreshUnattendedAsync);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_popups.IsShowing || _returningFromPopup)
        {
            _returningFromPopup = false;
            return;
        }

        _ = LoadAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _returningFromPopup = _popups.IsShowing;
    }

    private Task RefreshUnattendedAsync() =>
        DateTime.UtcNow - _lastLoadedUtc < ResumeRefresh.MinimumGap
            ? Task.CompletedTask
            : LoadAsync(silent: true);

    private async void OnPullToRefresh(object? sender, EventArgs e)
    {
        await LoadAsync();
        Refresher.IsRefreshing = false;
    }

    private void OnRetryClicked(object? sender, EventArgs e) => _ = LoadAsync();

    private async void OnBackTapped(object? sender, TappedEventArgs e) =>
        await this.GoBackAsync(AppShell.DashboardRoute);

    // ── Filters ──────────────────────────────────────────────────────────────

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        ClearSearchButton.IsVisible = !string.IsNullOrEmpty(e.NewTextValue);

        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _searchDebounceCts = cts;
        _ = DebouncedSearchAsync(e.NewTextValue, cts.Token);
    }

    private async Task DebouncedSearchAsync(string? text, CancellationToken ct)
    {
        try
        {
            await Task.Delay(SearchDebounce, ct);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        _search = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        await LoadAsync();
    }

    private void OnClearSearchClicked(object? sender, EventArgs e) => SearchEntry.Text = string.Empty;

    private async void OnUrgencyChipTapped(object? sender, TappedEventArgs e)
    {
        var choice = await _popups.ChooseAsync("How soon it asked you to act", "Cancel", UrgencyChoices);
        if (choice is null)
            return;

        _urgency = DaybookPresentation.UrgencyWireValue(choice);
        UrgencyChipLabel.Text = _urgency is null ? "Any urgency" : choice;
        await LoadAsync();
    }

    private async void OnWindowChipTapped(object? sender, TappedEventArgs e)
    {
        var choice = await _popups.ChooseAsync(
            "How far back to look", "Cancel", Windows.Select(w => w.Label).ToArray());
        if (choice is null)
            return;

        _windowDays = Windows.First(w => w.Label == choice).Days;
        WindowChipLabel.Text = choice;
        await LoadAsync();
    }

    private bool HasActiveFilter => _search is not null || _urgency is not null || _windowDays is not null;

    /// <summary>
    /// Which CardiMember's daybook to show. The chooser offers every member on the account; a
    /// choice clears nothing else — a caregiver comparing two members' weeks wants the same
    /// filters over both.
    /// </summary>
    private async void OnMemberChipTapped(object? sender, TappedEventArgs e)
    {
        var options = _members
            .Select(m => NameFormatting.FirstName(m.Name) ?? "Unnamed")
            .ToArray();

        var choice = await _popups.ChooseAsync("Whose daybook", "Cancel", options);
        if (choice is null)
            return;

        var index = Array.IndexOf(options, choice);
        if (index < 0 || _members[index].Id == _memberId)
            return;

        _memberId = _members[index].Id;
        _memberFirstName = NameFormatting.FirstName(_members[index].Name);
        MemberChipLabel.Text = choice;
        await LoadAsync();
    }

    // ── Loading ──────────────────────────────────────────────────────────────

    private async Task LoadAsync(bool silent = false)
    {
        if (_isLoading)
            return;
        _isLoading = true;

        if (!_hasLoadedOnce)
            SetState(loading: true);

        try
        {
            // A deep link names the member; it wins over both the remembered selection and the
            // primary-member rule below, because the affordance that sent the caregiver here
            // promised them this member's daybook.
            if (_pendingMemberId is { } pending)
            {
                _pendingMemberId = null;
                if (_members.Count == 0)
                    _members = await _api.GetCardiMembersAsync();

                if (_members.FirstOrDefault(m => m.Id == pending) is { } chosen)
                {
                    _memberId = chosen.Id;
                    _memberFirstName = NameFormatting.FirstName(chosen.Name);
                    MemberChipLabel.Text = _memberFirstName ?? "Member";
                    MemberChip.IsVisible = _members.Count > 1;
                }
            }

            // The same rule the dashboard and the device-setup launcher use for "which member",
            // so the three cannot drift apart about who the app means when it has not been told —
            // and the full list is kept, because the chooser above offers every member on the
            // account once there is more than one to choose between.
            if (_memberId == Guid.Empty)
            {
                _members = await _api.GetCardiMembersAsync();
                var member = PrimaryCardiMember.From(_members);
                if (member is null)
                {
                    EmptyDetailLabel.Text =
                        "Add the person you care about, and their days will be summarised here.";
                    SetState(empty: true);
                    return;
                }

                _memberId = member.Id;
                _memberFirstName = NameFormatting.FirstName(member.Name);
                MemberChipLabel.Text = _memberFirstName ?? "Member";
                MemberChip.IsVisible = _members.Count > 1;
            }

            var from = _windowDays is { } days
                ? DateOnly.FromDateTime(DateTime.Now).AddDays(-(days - 1))
                : (DateOnly?)null;

            var reviews = await _api.GetDaybooksAsync(
                _memberId, HistoryLimit, _search, from, _urgency);
            _lastLoadedUtc = DateTime.UtcNow;
            _hasLoadedOnce = true;
            _hasAnyReviews = _hasAnyReviews || reviews.Count > 0;

            // The filter row appears once the member has ever had a review to filter, and then
            // stays: hiding it on an empty *filtered* result would take away the one control
            // that undoes the emptiness.
            FilterPanel.IsVisible = _hasAnyReviews;

            if (reviews.Count == 0)
            {
                if (HasActiveFilter)
                {
                    EmptyTitleLabel.Text = "No entries match";
                    EmptyDetailLabel.Text =
                        "Nothing in their history matches these filters — clear one and look again.";
                }
                else
                {
                    var who = string.IsNullOrWhiteSpace(_memberFirstName)
                        ? "their"
                        : $"{_memberFirstName}'s";
                    EmptyTitleLabel.Text = "No daybook entries yet";
                    EmptyDetailLabel.Text =
                        $"The first entry is written after {who} first full day of readings.";
                }
                SetState(empty: true);
                return;
            }

            Render(reviews);
            SetState(loaded: true);
        }
        catch (ApiException ex)
        {
            if (!_hasLoadedOnce)
            {
                ErrorDetailLabel.Text = ex.Message;
                SetState(error: true);
            }
            else if (!silent)
            {
                // There is already a list on screen. Replacing it with an error panel would take
                // away reviews that are still perfectly readable — they describe finished days and
                // do not go stale — so the failure is said over the top of them instead.
                await _popups.ShowWarningAsync(ex.Message, "Couldn't refresh");
            }
        }
        finally
        {
            _isLoading = false;
            Refresher.IsRefreshing = false;
        }
    }

    private void Render(IReadOnlyList<DigestResponse> reviews)
    {
        ReviewsHost.Clear();

        foreach (var review in reviews)
            ReviewsHost.Add(BuildCard(review));
    }

    /// <summary>
    /// One day's card: when, what it was about, how soon it asks for attention, and the first
    /// lines of the review. Opening it is a navigation, not an expansion — the full account now
    /// carries the trend charts, which is more than a list row can hold and stay a list.
    /// </summary>
    private View BuildCard(DigestResponse review)
    {
        var body = new Label
        {
            Text = review.Text,
            Style = Styled("Body2Dark"),
            MaxLines = PreviewLines,
            LineBreakMode = LineBreakMode.TailTruncation,
        };

        // A small button, not a link line: "Read" is the card's one action said quietly, and
        // the whole card is tappable anyway — this is the visible affordance, not the only one.
        var read = new Border
        {
            StrokeThickness = 0.5,
            Stroke = Tinted("PrimaryDark"),
            BackgroundColor = Colors.Transparent,
            Padding = new Thickness(14, 5),
            HorizontalOptions = LayoutOptions.Start,
            StrokeShape = new RoundRectangle { CornerRadius = 13 },
            Content = new Label
            {
                Text = "Read",
                FontFamily = "QuicksandSemiBold",
                FontSize = 12,
                TextColor = Tinted("PrimaryDark"),
            },
        };
        SemanticProperties.SetDescription(read, "Read");
        SemanticProperties.SetHint(read, "Opens this day's full entry");

        // The alert tiles' construction, borrowed whole: a coloured rounded rect underneath and
        // the white card inset 4px from its left edge, so the urgency reads as a rail rounding
        // into the corner rather than as a pill spending space in the heading. No urgency, no
        // rail — the strip stays white rather than inventing a tier.
        var inner = new Border
        {
            BackgroundColor = Tinted("White"),
            StrokeThickness = 0,
            Padding = new Thickness(15, 14, 14, 14),
            StrokeShape = new RoundRectangle { CornerRadius = 20 },
            Content = new VerticalStackLayout
            {
                Spacing = 8,
                Children =
                {
                    Heading(review),
                    body,
                    read,
                },
            },
        };

        var card = new Border
        {
            BackgroundColor = DaybookPresentation.UrgencyRailColor(review.Urgency) ?? Tinted("White"),
            StrokeThickness = 0,
            Padding = new Thickness(4, 0, 0, 0),
            StrokeShape = new RoundRectangle { CornerRadius = 20 },
            Shadow = new Shadow
            {
                Brush = (Brush)Microsoft.Maui.Controls.Application.Current!.Resources["CardShadowBrush"],
                Opacity = 0.15f,
                Radius = 14,
                Offset = new Point(0, 4),
            },
            Content = inner,
        };

        var open = new TapGestureRecognizer
        {
            Command = new Command(async () => await Shell.Current.GoToAsync(
                $"{DaybookEntryPage.Route}?memberId={_memberId}&date={review.LocalDate:yyyy-MM-dd}"
                + $"&name={Uri.EscapeDataString(_memberFirstName ?? string.Empty)}")),
        };
        card.GestureRecognizers.Add(open);
        read.GestureRecognizers.Add(open);

        return card;
    }

    /// <summary>The date, the generated headline, and the urgency pill on one row.</summary>
    private static Grid Heading(DigestResponse review)
    {
        var heading = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
            ColumnSpacing = 10,
        };

        var titles = new VerticalStackLayout { Spacing = 2 };
        titles.Add(new Label
        {
            Text = DaybookPresentation.DayLabel(review.LocalDate),
            Style = Styled("Body1SemiBoldDark"),
        });
        titles.Add(new Label
        {
            // The review's own headline names what that day was about. Falling back to a fixed
            // label rather than leaving the row empty: entries written before headlines existed
            // have none, and the apps have always titled themselves in that case.
            Text = string.IsNullOrWhiteSpace(review.Headline) ? "The day in full" : review.Headline,
            Style = Styled("Body2"),
            TextColor = Tinted("BodyText"),
        });
        heading.Add(titles);

        // No pill: the card's left rail carries the urgency now, the way the alert tiles do.
        return heading;
    }

    /// <summary>
    /// A style from the merged application dictionary.
    /// </summary>
    /// <remarks>
    /// Not <c>this.Resources</c>: a page's own dictionary holds only what that page declares, and
    /// this one declares nothing — every style here lives in the dictionaries App.xaml merges, so
    /// the indexer on the page throws rather than walking up to them. The same reason
    /// <c>BottomNavBar</c> fully qualifies its lookups. Worth a helper because the failure is a
    /// <see cref="KeyNotFoundException"/> thrown while building a card, which surfaces as a list
    /// that never arrives rather than as an error a caregiver could act on.
    /// </remarks>
    private static Style Styled(string key) =>
        (Style)Microsoft.Maui.Controls.Application.Current!.Resources[key];

    /// <summary>A colour from the same place, for the same reason.</summary>
    private static Color Tinted(string key) =>
        (Color)Microsoft.Maui.Controls.Application.Current!.Resources[key];

    private void SetState(
        bool loading = false, bool loaded = false, bool empty = false, bool error = false)
    {
        SkeletonPanel.IsVisible = loading;
        ReviewsHost.IsVisible = loaded;
        EmptyPanel.IsVisible = empty;
        ErrorPanel.IsVisible = error;
    }
}
