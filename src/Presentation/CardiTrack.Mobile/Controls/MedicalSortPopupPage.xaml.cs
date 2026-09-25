using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Members;
using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific;
using Microsoft.Maui.Controls.Shapes;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// Sorts a block of medical notes — the single note from before the ledger — into separate lines,
/// the caregiver saying what each part is.
/// </summary>
/// <remarks>
/// <para>
/// The kind is never guessed. "Penicillin" reads like an allergy, but a caregiver may have written
/// it as a medicine, and a wrong guess about an allergy is worse than asking. Every part starts as
/// Other — what the whole block was — and the caregiver moves the ones that are something more.
/// </para>
/// <para>
/// A part can be skipped: an old note often holds something that stopped being true. Skipping
/// loses nothing, since the whole block goes to the history as it was.
/// </para>
/// </remarks>
public partial class MedicalSortPopupPage : ContentPage
{
    /// <summary>What a part can be set to: the four kinds, in the list's own order, and leaving it out.</summary>
    private static readonly IReadOnlyList<MedicalEntryKind?> Choices =
        [.. MedicalLedgerLines.DisplayOrder.Select(k => (MedicalEntryKind?)k), null];

    private readonly TaskCompletionSource<IReadOnlyList<(MedicalEntryKind Kind, string Text)>?> _result = new();
    private readonly List<Piece> _pieces = [];
    private bool _closing;

    private sealed class Piece
    {
        public required string Text { get; init; }
        public MedicalEntryKind? Kind { get; set; } = MedicalEntryKind.Other;
        public List<(MedicalEntryKind? Kind, Border Chip, Label Label)> Chips { get; } = [];
    }

    public MedicalSortPopupPage(IReadOnlyList<string> statements)
    {
        InitializeComponent();
        On<iOS>().SetModalPresentationStyle(UIModalPresentationStyle.OverFullScreen);

        foreach (var statement in statements)
        {
            var piece = new Piece { Text = statement };
            _pieces.Add(piece);
            PiecesHost.Add(BuildPiece(piece));
            Show(piece);
        }
    }

    /// <summary>The lines to file, in order, or null when cancelled or dismissed.</summary>
    public Task<IReadOnlyList<(MedicalEntryKind Kind, string Text)>?> Result => _result.Task;

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        PopupCard.Fit(Card, width);
        // The card is as tall as its parts, and the parts scroll only past about half the screen,
        // so the buttons stay in reach. Capping the card instead stretched a short list to the cap.
        PiecesScroller.MaximumHeightRequest = height * 0.5;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = Scrim.FadeToAsync(1, 140);
        _ = Card.ScaleToAsync(1, 140, Easing.CubicOut);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (!_closing && !Navigation.ModalStack.Contains(this))
            _result.TrySetResult(null);
    }

    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync(null);
        return true;
    }

    /// <summary>Tapping away closes only while nothing has been sorted, the rule the editors follow.</summary>
    private async void OnScrimTapped(object? sender, TappedEventArgs e)
    {
        if (_pieces.All(p => p.Kind == MedicalEntryKind.Other))
            await CloseAsync(null);
    }

    private async void OnCancelClicked(object? sender, EventArgs e) => await CloseAsync(null);

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        var lines = _pieces
            .Where(p => p.Kind is not null)
            .Select(p => (p.Kind!.Value, p.Text))
            .ToList();
        await CloseAsync(lines);
    }

    private View BuildPiece(Piece piece)
    {
        var text = new Label
        {
            Text = piece.Text,
            Style = Resource<Style>("Body2"),
            TextColor = Resource<Color>("HeadingText"),
        };

        // Two rows of chips rather than five across: at 12pt five labels do not fit a phone's card
        // without cutting "Medication" short, and a cut label is a wrong one.
        var chips = new Grid
        {
            ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star), new(GridLength.Star) },
            RowDefinitions = { new(GridLength.Auto), new(GridLength.Auto) },
            ColumnSpacing = 6,
            RowSpacing = 6,
        };
        for (var i = 0; i < Choices.Count; i++)
        {
            var kind = Choices[i];
            var label = new Label
            {
                Text = kind is { } k ? MedicalLedgerLines.KindName(k) : "Skip",
                FontFamily = "QuicksandSemiBold",
                FontSize = 12,
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center,
            };
            var chip = new Border
            {
                HeightRequest = 32,
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 10 },
                Content = label,
            };
            SemanticProperties.SetDescription(chip, $"{label.Text}: {piece.Text}");
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) =>
            {
                piece.Kind = kind;
                Show(piece);
            };
            chip.GestureRecognizers.Add(tap);
            Grid.SetColumn(chip, i % 3);
            Grid.SetRow(chip, i / 3);
            chips.Add(chip);
            piece.Chips.Add((kind, chip, label));
        }

        return new VerticalStackLayout { Spacing = 8, Children = { text, chips } };
    }

    /// <summary>
    /// The set choice filled: blue for a kind, dark for Skip — the same colours the action buttons
    /// give moving forward and backing out.
    /// </summary>
    private static void Show(Piece piece)
    {
        foreach (var (kind, chip, label) in piece.Chips)
        {
            var set = kind == piece.Kind;
            var fill = kind is null ? "OffBlack" : "Primary";
            chip.BackgroundColor = set ? Resource<Color>(fill) : Resource<Color>("White");
            chip.Stroke = set ? Resource<Color>(fill) : Resource<Color>("Divider");
            label.TextColor = set ? Resource<Color>("White") : Resource<Color>("HeadingText");
            SemanticProperties.SetHint(chip, set ? "Selected" : string.Empty);
        }
    }

    private static T Resource<T>(string key) =>
        Microsoft.Maui.Controls.Application.Current!.Resources.TryGetValue(key, out var value) && value is T t
            ? t
            : default!;

    private async Task CloseAsync(IReadOnlyList<(MedicalEntryKind Kind, string Text)>? lines)
    {
        if (_closing)
            return;
        _closing = true;

        try
        {
            await Task.WhenAll(
                Scrim.FadeToAsync(0, 100),
                Card.ScaleToAsync(0.92, 100, Easing.CubicIn));
            await Navigation.PopModalAsync(animated: false);
        }
        finally
        {
            _result.TrySetResult(lines);
        }
    }
}
