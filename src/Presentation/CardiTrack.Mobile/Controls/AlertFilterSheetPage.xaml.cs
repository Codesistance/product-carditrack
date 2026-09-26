using System.Globalization;
using CardiTrack.Mobile.Core.Alerts;
using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific;
using Microsoft.Maui.Controls.Shapes;

namespace CardiTrack.Mobile.Controls;

/// <summary>A CardiMember the sheet can narrow the list to.</summary>
public sealed record AlertFilterMember(Guid Id, string Name);

/// <summary>
/// The Alerts list's filter sheet: whose alerts, which of them, how serious, and since when.
/// Completes <see cref="Result"/> with the filter to apply, or null when dismissed.
/// </summary>
/// <remarks>
/// The caregiver edits a draft. Nothing reaches the list until "Show", so the count on that
/// button — the draft asked of the API as it changes — is the only thing that moves while the
/// sheet is up.
/// </remarks>
public partial class AlertFilterSheetPage : ContentPage
{
    /// <summary>How much of the page the sections may take before they scroll.</summary>
    private const double SectionsShareOfPage = 0.6;

    /// <summary>
    /// How long the draft has to sit still before it is counted. A caregiver tapping across a row
    /// to the chip they want would otherwise send a request per chip they passed.
    /// </summary>
    private static readonly TimeSpan CountDebounce = TimeSpan.FromMilliseconds(250);

    private readonly TaskCompletionSource<AlertListFilter?> _result = new();
    private readonly Func<AlertListFilter, CancellationToken, Task<int?>> _count;
    private readonly List<Action> _repaints = [];
    private AlertListFilter _draft;
    private CancellationTokenSource? _countCts;
    private bool _closing;

    /// <param name="current">The filter the list is showing now — the draft starts as it.</param>
    /// <param name="members">Whom the list can be narrowed to, in the order to offer them.</param>
    /// <param name="archived">
    /// Whether the list is the archive, where "which" does not apply: every alert there is
    /// resolved, so the section is left out rather than offered and ignored.
    /// </param>
    /// <param name="count">How many alerts a filter would show, or null when that is not known.</param>
    public AlertFilterSheetPage(
        AlertListFilter current,
        IReadOnlyList<AlertFilterMember> members,
        bool archived,
        Func<AlertListFilter, CancellationToken, Task<int?>> count)
    {
        InitializeComponent();
        // Without OverFullScreen, iOS removes the page underneath and the transparent modal
        // renders over black.
        On<iOS>().SetModalPresentationStyle(UIModalPresentationStyle.OverFullScreen);

        _draft = current;
        _count = count;

        // The member the list is already narrowed to is always offered, even when the member list
        // could not be read or no longer has them — otherwise the sheet could not show what is set.
        var offered = members.ToList();
        if (current.MemberId is { } setId && offered.All(m => m.Id != setId))
            offered.Insert(0, new AlertFilterMember(setId, current.MemberName ?? AlertListFilter.UnnamedMemberLabel));

        SectionsHost.Add(Section(
            "Whose",
            [
                Choice("Everyone", null, () => _draft.MemberId is null, () => _draft = _draft with { MemberId = null, MemberName = null }),
                .. offered.Select(m => Choice(
                    m.Name, null,
                    () => _draft.MemberId == m.Id,
                    () => _draft = _draft with { MemberId = m.Id, MemberName = m.Name })),
            ]));

        if (!archived)
        {
            SectionsHost.Add(Section(
                "Which",
                [.. Enum.GetValues<AlertStatusChoice>().Select(s => Choice(
                    AlertListFilter.StatusLabel(s), null,
                    () => _draft.Status == s,
                    () => _draft = _draft with { Status = s }))]));
        }

        SectionsHost.Add(Section(
            "How serious",
            [.. Enum.GetValues<AlertSeverityChoice>().Select(s => Choice(
                AlertListFilter.SeverityLabel(s), SeverityColour(s),
                () => _draft.Severity == s,
                () => _draft = _draft with { Severity = s }))]));

        SectionsHost.Add(Section(
            "When",
            [.. Enum.GetValues<AlertWindow>().Select(w => Choice(
                AlertListFilter.WindowLabel(w), null,
                () => _draft.Window == w,
                () => _draft = _draft with { Window = w }))]));

        Repaint();
    }

    public Task<AlertListFilter?> Result => _result.Task;

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (height > 0)
            SectionsScroll.MaximumHeightRequest = height * SectionsShareOfPage;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = Scrim.FadeToAsync(1, 140);
        _ = Card.TranslateToAsync(0, 0, 180, Easing.CubicOut);
        _ = CountDraftAsync(debounce: false);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // Also fires on backgrounding and when another modal covers this one; only an external
        // dismissal — one that took the page off the stack without CloseAsync — resolves here.
        if (!_closing && !Navigation.ModalStack.Contains(this))
        {
            _countCts?.Cancel();
            _result.TrySetResult(null);
        }
    }

    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync(null);
        return true;
    }

    private async void OnScrimTapped(object? sender, TappedEventArgs e) => await CloseAsync(null);

    private void OnResetClicked(object? sender, EventArgs e)
    {
        _draft = AlertListFilter.None;
        Repaint();
        _ = CountDraftAsync(debounce: false);
    }

    private async void OnShowClicked(object? sender, EventArgs e) => await CloseAsync(_draft);

    private static View Section(string title, IReadOnlyList<View> chips)
    {
        var row = new FlexLayout
        {
            Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap,
            Direction = Microsoft.Maui.Layouts.FlexDirection.Row,
            AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.Start,
        };
        foreach (var chip in chips)
        {
            // Wrap, never squeeze: FlexLayout shrinks children by default, which cut the last
            // letters off "All open" and "Acknowledged" rather than moving them to the next line.
            FlexLayout.SetShrink(chip, 0);
            row.Add(chip);
        }

        return new VerticalStackLayout
        {
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0),
            Children =
            {
                new Label
                {
                    Text = title,
                    FontFamily = "QuicksandSemiBold",
                    FontSize = 14,
                    TextColor = Resource<Color>("HeadingText"),
                },
                row,
            },
        };
    }

    /// <summary>
    /// One choice as a pill: the gradient fill and white label when set, a hairline PrimaryDark
    /// outline when not — the chip language the list's filter row used, so a caregiver who knew
    /// that row knows these.
    /// </summary>
    /// <param name="dot">A severity's colour, shown before its word; null for none.</param>
    private View Choice(string text, Color? dot, Func<bool> isSet, Action set)
    {
        var label = new Label
        {
            Text = text,
            FontFamily = "QuicksandSemiBold",
            FontSize = 14,
            VerticalTextAlignment = TextAlignment.Center,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaximumWidthRequest = 200,
        };

        var content = new HorizontalStackLayout { Spacing = 6 };
        if (dot is not null)
        {
            content.Add(new Ellipse
            {
                Fill = new SolidColorBrush(dot),
                WidthRequest = 8,
                HeightRequest = 8,
                VerticalOptions = LayoutOptions.Center,
            });
        }
        content.Add(label);

        var chip = new Border
        {
            Padding = new Thickness(14, 7),
            Margin = new Thickness(0, 0, 8, 8),
            MinimumHeightRequest = 36,
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            Content = content,
        };
        SemanticProperties.SetDescription(chip, text);

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            if (isSet())
                return;
            set();
            Repaint();
            _ = CountDraftAsync(debounce: true);
        };
        chip.GestureRecognizers.Add(tap);

        _repaints.Add(() =>
        {
            var on = isSet();
            chip.Background = on ? Resource<Brush>("GradientButtonBrush") : null;
            chip.BackgroundColor = on ? null : Resource<Color>("White");
            chip.Stroke = on ? null : Resource<Color>("PrimaryDark");
            chip.StrokeThickness = on ? 0 : 0.5;
            label.TextColor = Resource<Color>(on ? "White" : "HeadingText");
            SemanticProperties.SetHint(chip, on ? "Selected" : string.Empty);
        });
        return chip;
    }

    private void Repaint()
    {
        foreach (var repaint in _repaints)
            repaint();
    }

    /// <summary>
    /// Asks how many alerts the draft would show and puts it on the button. Superseded counts are
    /// cancelled, so the button can only ever describe the draft as it is now; a count that
    /// cannot be had leaves the button saying "Show alerts" rather than a number it does not know.
    /// </summary>
    private async Task CountDraftAsync(bool debounce)
    {
        _countCts?.Cancel();
        var cts = _countCts = new CancellationTokenSource();
        var draft = _draft;
        ShowButton.Text = "Show alerts";

        try
        {
            if (debounce)
                await Task.Delay(CountDebounce, cts.Token);

            var total = await _count(draft, cts.Token);
            if (cts.IsCancellationRequested || total is not { } n)
                return;

            ShowButton.Text = n switch
            {
                0 => "No alerts match",
                1 => "Show 1 alert",
                _ => string.Create(CultureInfo.CurrentCulture, $"Show {n} alerts"),
            };
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>The colour the alert cards give a severity; none for "Any".</summary>
    private static Color? SeverityColour(AlertSeverityChoice severity) => severity switch
    {
        AlertSeverityChoice.Critical => Resource<Color>("StatusRed"),
        AlertSeverityChoice.Urgent => Resource<Color>("StatusOrange"),
        AlertSeverityChoice.Notice => Resource<Color>("StatusYellow"),
        AlertSeverityChoice.Info => Resource<Color>("StatusGreen"),
        _ => null,
    };

    private static T Resource<T>(string key) =>
        (T)Microsoft.Maui.Controls.Application.Current!.Resources[key];

    private async Task CloseAsync(AlertListFilter? filter)
    {
        if (_closing)
            return;
        _closing = true;
        _countCts?.Cancel();

        try
        {
            await Task.WhenAll(
                Scrim.FadeToAsync(0, 100),
                Card.TranslateToAsync(0, 40, 120, Easing.CubicIn));
            await Navigation.PopModalAsync(animated: false);
        }
        finally
        {
            _result.TrySetResult(filter);
        }
    }
}
