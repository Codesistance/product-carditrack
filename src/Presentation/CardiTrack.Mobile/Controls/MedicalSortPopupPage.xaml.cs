using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Members;
using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific;

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
        public List<(MedicalEntryKind? Kind, SelectChip Chip)> Chips { get; } = [];
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

        // The app's pick-one chips (SelectChip), wrapped rather than laid in a fixed grid: at the
        // chip's 14 points five words do not fit across a phone's card, nor does "Medication" fit a
        // third of it, and a cut label is a wrong one. Wrapping, never squeezing — FlexLayout
        // shrinks children by default, the same trap the filter sheet's chips fell into.
        var chips = new FlexLayout
        {
            Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap,
            Direction = Microsoft.Maui.Layouts.FlexDirection.Row,
            AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.Start,
        };
        foreach (var kind in Choices)
        {
            var chip = new SelectChip
            {
                Text = kind is { } k ? MedicalLedgerLines.KindName(k) : "Skip",
                Margin = new Thickness(0, 0, 8, 8),
            };
            SemanticProperties.SetDescription(chip, $"{chip.Text}: {piece.Text}");
            chip.Tapped += (_, _) =>
            {
                piece.Kind = kind;
                Show(piece);
            };
            FlexLayout.SetShrink(chip, 0);
            chips.Add(chip);
            piece.Chips.Add((kind, chip));
        }

        return new VerticalStackLayout { Spacing = 8, Children = { text, chips } };
    }

    /// <summary>
    /// The set choice filled, Skip included: skipping a part is a choice like the others, not a
    /// way out of the form — that is the Cancel button's.
    /// </summary>
    private static void Show(Piece piece)
    {
        foreach (var (kind, chip) in piece.Chips)
            chip.IsSelected = kind == piece.Kind;
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
