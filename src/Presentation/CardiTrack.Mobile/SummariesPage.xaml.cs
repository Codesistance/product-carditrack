using System.Globalization;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Onboarding;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

/// <summary>
/// The Summaries tab: the member's day reviews, newest first, one per finished day.
/// </summary>
/// <remarks>
/// <para>
/// Took the Family tab's slot. That page was a placeholder for family invitations, which are R3
/// work — a permanent quarter of the bottom navigation spent on a card that said "coming soon",
/// while the day reviews had no surface at all. When family sharing does land it belongs under
/// Settings or scoped to a member, not back in the bar.
/// </para>
/// <para>
/// Refreshes on resume but does not poll. Every other live surface in the app carries
/// <c>RefreshEvery(PeriodicRefresh.LiveDataInterval)</c> because what it shows can change within
/// the minute; a day review is written once, at 02:00 in the member's own local time, and cannot
/// change afterwards. Polling it would be a request every thirty seconds for a list that moves
/// once a day.
/// </para>
/// </remarks>
public partial class SummariesPage : ContentPage
{
    /// <summary>
    /// How many days back the list reaches. A fortnight is the window the trend charts use and is
    /// about as far back as a caregiver reads; the service clamps anything larger anyway.
    /// </summary>
    private const int HistoryLimit = 14;

    /// <summary>Lines of the review shown before it is opened.</summary>
    private const int CollapsedLines = 3;

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;

    private bool _isLoading;
    private bool _returningFromPopup;
    private DateTime _lastLoadedUtc = DateTime.MinValue;
    private bool _hasLoadedOnce;

    public SummariesPage(ICardiTrackApiClient api, IPopupService popups)
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

    private async Task LoadAsync(bool silent = false)
    {
        if (_isLoading)
            return;
        _isLoading = true;

        if (!_hasLoadedOnce)
            SetState(loading: true);

        try
        {
            // The same rule the dashboard and the device-setup launcher use for "which member",
            // so the three cannot drift apart about who the app means when it has not been told.
            var member = PrimaryCardiMember.From(await _api.GetCardiMembersAsync());
            if (member is null)
            {
                EmptyDetailLabel.Text =
                    "Add the person you care about, and their days will be summarised here.";
                SetState(empty: true);
                return;
            }

            var reviews = await _api.GetDayReviewsAsync(member.Id, HistoryLimit);
            _lastLoadedUtc = DateTime.UtcNow;
            _hasLoadedOnce = true;

            if (reviews.Count == 0)
            {
                var firstName = NameFormatting.FirstName(member.Name);
                var who = string.IsNullOrWhiteSpace(firstName) ? "their" : $"{firstName}'s";
                EmptyDetailLabel.Text =
                    $"The first review is written after {who} first full day of readings.";
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
    /// One day's card: when, what it was about, how soon it asks for attention, and the review
    /// itself — clipped until it is opened, because a twelve-sentence account of every day at full
    /// height is a list nobody can scan.
    /// </summary>
    private View BuildCard(DigestResponse review)
    {
        var body = new Label
        {
            Text = review.Text,
            Style = Styled("Body2Dark"),
            MaxLines = CollapsedLines,
            LineBreakMode = LineBreakMode.TailTruncation,
        };

        var suggestion = new Label
        {
            Text = review.Suggestion,
            Style = Styled("Body2"),
            TextColor = Tinted("BodyText"),
            IsVisible = false,
        };

        var more = new Label
        {
            Text = "Read the full day",
            Style = Styled("SectionLink"),
        };

        var card = new Border
        {
            Style = Styled("ElevatedCard"),
            Content = new VerticalStackLayout
            {
                Spacing = 8,
                Children =
                {
                    Heading(review),
                    body,
                    suggestion,
                    more,
                },
            },
        };

        card.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() =>
            {
                var opening = body.MaxLines == CollapsedLines;
                body.MaxLines = opening ? -1 : CollapsedLines;
                body.LineBreakMode = opening ? LineBreakMode.WordWrap : LineBreakMode.TailTruncation;
                suggestion.IsVisible = opening && !string.IsNullOrWhiteSpace(review.Suggestion);
                more.Text = opening ? "Show less" : "Read the full day";
            }),
        });

        return card;
    }

    /// <summary>The date, the generated headline, and the urgency pill on one row.</summary>
    private Grid Heading(DigestResponse review)
    {
        var heading = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
            ColumnSpacing = 10,
        };

        var titles = new VerticalStackLayout { Spacing = 2 };
        titles.Add(new Label
        {
            Text = DayLabel(review.LocalDate),
            Style = Styled("Body1SemiBoldDark"),
        });
        titles.Add(new Label
        {
            // The review's own headline names what that day was about. Falling back to a fixed
            // label rather than leaving the row empty: entries written before headlines existed
            // have none, and the apps have always titled themselves in that case.
            Text = string.IsNullOrWhiteSpace(review.Headline) ? "A day in review" : review.Headline,
            Style = Styled("Body2"),
            TextColor = Tinted("BodyText"),
        });
        heading.Add(titles);

        if (UrgencyPill(review.Urgency) is { } pill)
        {
            Grid.SetColumn(pill, 1);
            heading.Add(pill);
        }

        return heading;
    }

    /// <summary>
    /// The model's own read of how soon the family should act, as a pill — or nothing at all when
    /// it returned none, or one this app does not know. A pill is a claim about a member's health,
    /// so an unrecognised value shows no pill rather than a grey one implying the service judged
    /// the day and found it unremarkable.
    /// </summary>
    private static Border? UrgencyPill(string? urgency)
    {
        var (text, colourKey) = urgency switch
        {
            "watch" => ("WATCH", "StatusGreen"),
            "check-in" => ("CHECK IN", "StatusYellow"),
            "concerning" => ("CONCERNING", "StatusOrange"),
            "act-now" => ("ACT NOW", "StatusRed"),
            _ => (null, null),
        };

        if (text is null || colourKey is null)
            return null;

        return new Border
        {
            Style = Styled("StatusPill"),
            BackgroundColor = Tinted(colourKey),
            VerticalOptions = LayoutOptions.Start,
            Content = new Label
            {
                Text = text,
                Style = Styled("StatusPillText"),
                TextColor = Tinted("White"),
            },
        };
    }

    /// <summary>
    /// "Yesterday" for the day just gone, the weekday for the rest of the week, and the date
    /// beyond that — the way someone talks about their own week rather than a row of dates.
    /// </summary>
    /// <remarks>
    /// Against the device's today, not the member's: this is the reader's label for when they are
    /// reading, and a caregiver in another timezone reading "Yesterday" about the day their own
    /// yesterday was is the sentence they would have said themselves.
    /// </remarks>
    private static string DayLabel(DateOnly date)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var age = today.DayNumber - date.DayNumber;

        return age switch
        {
            0 => "Today",
            1 => "Yesterday",
            > 1 and < 7 => date.ToString("dddd", CultureInfo.CurrentCulture),
            _ => date.ToString("d MMMM", CultureInfo.CurrentCulture),
        };
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
