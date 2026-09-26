using System.Globalization;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Notifications;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// The "Complete the picture" card: per-member set-up rings, then every other open
/// data-completeness item in a sideways row. Shared by the Dashboard and Alerts.
/// </summary>
/// <remarks>
/// Safety banners are not in it. They say monitoring is degraded, which is not housekeeping, and
/// each screen puts them above everything else of its own; a caller passes only the rest.
/// </remarks>
public partial class CompleteThePictureCard : ContentView
{
    /// <summary>
    /// How much of the card's content width each item in the row takes — the Recent Alerts
    /// carousel's rule, so the next item peeks in at the edge.
    /// </summary>
    private const double RowItemWidthFraction = 0.85;

    public CompleteThePictureCard()
    {
        InitializeComponent();
        NudgeHeader.SizeChanged += (_, _) => SizeNudgeRows();
    }

    /// <summary>Whether a real answer — live or saved — has been drawn since the card was built.</summary>
    public bool HasContent { get; private set; }

    /// <summary>Whether the skeleton is standing in for a first answer that has not come yet.</summary>
    public bool IsLoading => SkeletonRows.IsVisible;

    /// <summary>
    /// Shows the card as loading — its title over two placeholder rows — while the first answer is
    /// on its way. Does nothing once something real has been drawn: a card with content keeps it
    /// until newer content replaces it.
    /// </summary>
    public void ShowLoading()
    {
        if (HasContent)
            return;

        SetupRingList.IsVisible = false;
        SingleNudgeHost.IsVisible = false;
        NudgeScroller.IsVisible = false;
        NudgeCountBadge.IsVisible = false;
        SeeAllLink.IsVisible = false;
        SkeletonRows.IsVisible = true;
        IsVisible = true;
    }

    /// <summary>
    /// Takes the card out of its loading state when the first answer could not be had: with
    /// nothing to show, it goes rather than pulsing forever.
    /// </summary>
    public void EndLoadingWithoutAnswer()
    {
        if (!IsLoading)
            return;
        SkeletonRows.IsVisible = false;
        IsVisible = false;
    }

    /// <summary>Draws a saved summary — the device's last answer — while the live one is fetched.</summary>
    public void ShowSaved(NotificationSummaryResponse summary) => Render(summary, summary.DashboardCards);

    /// <summary>
    /// Renders the summary's top items, then — when more are waiting than the summary carries —
    /// the rest from the inbox's own list, so the row can scroll through all of them.
    /// </summary>
    /// <remarks>
    /// A failure fetching the rest is swallowed: the top items are already on screen, and losing
    /// the tail leaves them there rather than blanking a card that was fine a moment ago. The
    /// summary itself is the caller's to fetch, since it also carries the safety banners the
    /// caller draws. A cancelled token stops the second render, so a superseded load cannot paint
    /// over a newer one.
    /// </remarks>
    public async Task LoadAsync(
        ICardiTrackApiClient api, NotificationSummaryResponse summary, CancellationToken ct = default)
    {
        Render(summary, summary.DashboardCards);

        if (WaitingNudges(summary) <= summary.DashboardCards.Count)
            return;

        try
        {
            var open = await api.GetNotificationsAsync(state: nameof(NotificationState.Open), owned: true, ct: ct);
            if (ct.IsCancellationRequested)
                return;

            // After the summary's items, which keep their places: the summary ranks by priority
            // and the list is the inbox's order. The list's total, not the summary's count, is
            // how many are waiting: the summary counts only the top items it projects, and the
            // list is one page of the inbox, so either can stop short of the real number. Safety
            // items are in the total and not in this row.
            var shown = summary.DashboardCards.Select(card => card.Id).ToHashSet();
            Render(summary,
            [
                .. summary.DashboardCards,
                .. open.Items.Where(n => n.Category != NotificationCategory.Safety && !shown.Contains(n.Id)),
            ],
            waiting: Math.Max(0, open.TotalCount - summary.SafetyBanners.Count));
        }
        catch (ApiException)
        {
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>Open items for this card — everything but the safety banners.</summary>
    private static int WaitingNudges(NotificationSummaryResponse summary) =>
        Math.Max(0, summary.OpenCount - summary.SafetyBanners.Count);

    /// <param name="waiting">
    /// How many items are open for this card in all, when known better than the summary knows it —
    /// see <see cref="LoadAsync"/>. Defaults to the summary's own count.
    /// </param>
    private void Render(
        NotificationSummaryResponse summary, IReadOnlyList<NotificationResponse> cards, int? waiting = null)
    {
        NudgeList.Clear();
        SingleNudgeHost.Content = null;
        SkeletonRows.IsVisible = false;
        HasContent = true;

        // Set-up progress as rings, one per member with something left to do. The reminders a ring
        // stands for leave the rows, so one missing emergency contact is asked about once.
        var rings = SetupProgressLine.For(summary.MemberSetup);
        var ringed = rings.Select(r => r.CardiMemberId).ToHashSet();
        bool StandsInARing(NotificationResponse n) =>
            n.CardiMemberId is { } id && ringed.Contains(id) && SetupProgressLine.SetupRuleCodes.Contains(n.RuleCode);
        var folded = cards.Count(StandsInARing);
        cards = cards.Where(n => !StandsInARing(n)).ToList();

        SetupRingList.Clear();
        foreach (var ring in rings)
            SetupRingList.Add(BuildSetupRing(ring));
        SetupRingList.IsVisible = rings.Count > 0;

        // One item fills the card; two or more scroll sideways — see SizeNudgeRows.
        foreach (var card in cards)
        {
            var row = new NudgeMiniRow(card);
            row.Tapped += OnNudgeTapped;
            if (cards.Count == 1)
                SingleNudgeHost.Content = row;
            else
                NudgeList.Add(row);
        }
        SingleNudgeHost.IsVisible = cards.Count == 1;
        NudgeScroller.IsVisible = cards.Count > 1;
        SizeNudgeRows();

        IsVisible = cards.Count + rings.Count > 0;

        // How many are waiting, on the title, so a caregiver knows there is more than the one in
        // view before they swipe. Not on a lone item — "1" beside a single card is the card again.
        // Each ring counts once, however many of its member's reminders it folded in.
        var rows = Math.Max((waiting ?? WaitingNudges(summary)) - folded, cards.Count);
        var total = rows + rings.Count;
        NudgeCountBadge.IsVisible = total > 1;
        NudgeCountLabel.Text = total > 9 ? "9+" : total.ToString(CultureInfo.CurrentCulture);
        SemanticProperties.SetDescription(NudgeCountBadge, $"{total} to complete");

        // The link is only worth offering when there is more behind it than the row can show.
        SeeAllLink.IsVisible = rows > cards.Count;
    }

    /// <summary>A member's set-up ring, title and next step, opening that step on a tap.</summary>
    private static View BuildSetupRing(SetupProgressLine line)
    {
        var resources = Microsoft.Maui.Controls.Application.Current!.Resources;

        var row = new Grid
        {
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            ColumnSpacing = 10,
            Padding = new Thickness(10, 7),
        };
        row.Add(new ProgressRing
        {
            Progress = line.Fraction,
            WidthRequest = 28,
            HeightRequest = 28,
            VerticalOptions = LayoutOptions.Center,
        }, 0, 0);
        row.Add(new VerticalStackLayout
        {
            Spacing = 0,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                new Label { Text = line.Title, Style = (Style)resources["Body1SemiBoldDark"], FontSize = 14 },
                new Label
                {
                    Text = line.Next,
                    Style = (Style)resources["Body2"],
                    FontSize = 12,
                    LineBreakMode = LineBreakMode.TailTruncation,
                },
            },
        }, 1, 0);
        row.Add(new Image
        {
            Source = "icon_chevron.svg",
            WidthRequest = 16,
            HeightRequest = 16,
            VerticalOptions = LayoutOptions.Center,
        }, 2, 0);

        var card = new Border
        {
            StrokeThickness = 0,
            BackgroundColor = (Color)resources["InputBackground"],
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
            Content = row,
        };
        SemanticProperties.SetDescription(card, $"{line.Title}. {line.Next}");
        SemanticProperties.SetHint(card, "Double tap to do the next step");

        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await OpenSetupStepAsync(line.NextStep);
        card.GestureRecognizers.Add(tap);
        return card;
    }

    /// <summary>
    /// The step's own screen. The time zone is answered on the reminders page, which confirms the
    /// phone's zone in a tap; a step whose screen this version does not have goes there too.
    /// </summary>
    private static async Task OpenSetupStepAsync(MemberSetupStep step)
    {
        var link = NudgeLinkParser.Parse(step.ActionDeepLink);
        var destination = link.Kind == NudgeDestinationKind.TimeZone ? null : DeepLinkRouter.Resolve(link);
        await Shell.Current.GoToAsync(destination ?? NotificationsPage.Route);
    }

    /// <summary>
    /// Sizes the row's items to most of the card's content width so the next item peeks in at
    /// the edge. Measured off the heading, since the row itself runs edge to edge.
    /// </summary>
    private void SizeNudgeRows()
    {
        if (NudgeHeader.Width <= 0)
            return;

        var width = Math.Floor(NudgeHeader.Width * RowItemWidthFraction);
        foreach (var row in NudgeList.Children.OfType<NudgeMiniRow>())
            row.WidthRequest = width;
    }

    private async void OnNudgeTapped(object? sender, NotificationResponse notification) =>
        await Shell.Current.GoToAsync(NotificationsPage.Route);

    private async void OnSeeAllTapped(object? sender, TappedEventArgs e) =>
        await Shell.Current.GoToAsync(NotificationsPage.Route);
}
