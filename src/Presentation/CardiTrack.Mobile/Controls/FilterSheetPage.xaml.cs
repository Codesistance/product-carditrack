using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific;
using Microsoft.Maui.Controls.Shapes;

namespace CardiTrack.Mobile.Controls;

/// <summary>A CardiMember a filter sheet can narrow a list to.</summary>
public sealed record FilterMember(Guid Id, string Name);

/// <summary>
/// One chip in a <see cref="FilterSheetPage"/> section. The draft it reads and writes is the
/// caller's, held in the closures, so the sheet never needs to know what kind of filter it edits.
/// </summary>
/// <param name="Dot">A colour shown before the words (a severity, an urgency); null for none.</param>
public sealed record FilterChoice(string Text, Color? Dot, Func<bool> IsSet, Action Set);

/// <summary>One question in a <see cref="FilterSheetPage"/>, answered by exactly one chip.</summary>
public sealed record FilterSection(string Title, IReadOnlyList<FilterChoice> Choices);

/// <summary>
/// A list's filter sheet: a stack of single-choice sections over a Reset and a Show button that
/// counts what the draft would show. Completes <see cref="Result"/> with true when the caregiver
/// asked to see the results, false when they dismissed it.
/// </summary>
/// <remarks>
/// <para>
/// The caregiver edits a draft. Nothing reaches the list until "Show", so the count on that
/// button — the draft asked of the API as it changes — is the only thing that moves while the
/// sheet is up.
/// </para>
/// <para>
/// Only the shell. What the sections ask, what the draft is and how it is counted belong to the
/// list it filters — see <see cref="AlertFilterSheet"/> and <see cref="JournalFilterSheet"/>.
/// </para>
/// </remarks>
public partial class FilterSheetPage : ContentPage
{
    /// <summary>How much of the page the sections may take before they scroll.</summary>
    private const double SectionsShareOfPage = 0.6;

    /// <summary>
    /// How long the draft has to sit still before it is counted. A caregiver tapping across a row
    /// to the chip they want would otherwise send a request per chip they passed.
    /// </summary>
    private static readonly TimeSpan CountDebounce = TimeSpan.FromMilliseconds(250);

    private readonly TaskCompletionSource<bool> _result = new();
    private readonly Action _reset;
    private readonly Func<CancellationToken, Task<string?>> _countLabel;
    private readonly string _idleLabel;
    private readonly List<Action> _repaints = [];
    private CancellationTokenSource? _countCts;
    private bool _closing;

    /// <param name="title">What the sheet filters, as its heading.</param>
    /// <param name="sections">The questions, in the order to ask them.</param>
    /// <param name="reset">Puts the caller's draft back to nothing narrowed.</param>
    /// <param name="countLabel">
    /// The Show button's words for the draft as it is when called — "Show 3 alerts" — or null when
    /// the count cannot be had, which leaves <paramref name="idleLabel"/> up rather than a number
    /// the sheet does not know.
    /// </param>
    /// <param name="idleLabel">The Show button's words while nothing is counted.</param>
    public FilterSheetPage(
        string title,
        IReadOnlyList<FilterSection> sections,
        Action reset,
        Func<CancellationToken, Task<string?>> countLabel,
        string idleLabel)
    {
        InitializeComponent();
        // Without OverFullScreen, iOS removes the page underneath and the transparent modal
        // renders over black.
        On<iOS>().SetModalPresentationStyle(UIModalPresentationStyle.OverFullScreen);

        _reset = reset;
        _countLabel = countLabel;
        _idleLabel = idleLabel;
        TitleLabel.Text = title;
        ShowButton.Text = idleLabel;

        foreach (var section in sections)
            SectionsHost.Add(Section(section.Title, [.. section.Choices.Select(Choice)]));

        Repaint();
    }

    public Task<bool> Result => _result.Task;

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
            _result.TrySetResult(false);
        }
    }

    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync(false);
        return true;
    }

    private async void OnScrimTapped(object? sender, TappedEventArgs e) => await CloseAsync(false);

    private void OnResetClicked(object? sender, EventArgs e)
    {
        _reset();
        Repaint();
        _ = CountDraftAsync(debounce: false);
    }

    private async void OnShowClicked(object? sender, EventArgs e) => await CloseAsync(true);

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
    /// outline when not — the chip language the Alerts list's old filter row used, so a caregiver
    /// who knew that row knows these.
    /// </summary>
    private View Choice(FilterChoice choice)
    {
        var label = new Label
        {
            Text = choice.Text,
            FontFamily = "QuicksandSemiBold",
            FontSize = 14,
            VerticalTextAlignment = TextAlignment.Center,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaximumWidthRequest = 200,
        };

        var content = new HorizontalStackLayout { Spacing = 6 };
        if (choice.Dot is { } dot)
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
        SemanticProperties.SetDescription(chip, choice.Text);

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            if (choice.IsSet())
                return;
            choice.Set();
            Repaint();
            _ = CountDraftAsync(debounce: true);
        };
        chip.GestureRecognizers.Add(tap);

        _repaints.Add(() =>
        {
            var on = choice.IsSet();
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
    /// Asks what the draft would show and puts it on the button. Superseded counts are cancelled,
    /// so the button can only ever describe the draft as it is now.
    /// </summary>
    private async Task CountDraftAsync(bool debounce)
    {
        if (_countCts is { } previous)
        {
            previous.Cancel();
            previous.Dispose();
        }
        var cts = _countCts = new CancellationTokenSource();
        ShowButton.Text = _idleLabel;

        try
        {
            if (debounce)
                await Task.Delay(CountDebounce, cts.Token);

            var text = await _countLabel(cts.Token);
            if (cts.IsCancellationRequested || text is null)
                return;

            ShowButton.Text = text;
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal static T Resource<T>(string key) =>
        (T)Microsoft.Maui.Controls.Application.Current!.Resources[key];

    private async Task CloseAsync(bool show)
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
            _result.TrySetResult(show);
        }
    }
}
