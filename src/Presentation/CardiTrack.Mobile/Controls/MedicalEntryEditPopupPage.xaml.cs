using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Members;
using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// Adds or changes one line of a member's medical information: its kind and its words.
/// </summary>
/// <remarks>
/// It replaced the single-note form, which edited the whole record as one block. Same shell as
/// <see cref="ContactEditPopupPage"/>, with the same rules for leaving: tapping away closes it only
/// while nothing has been typed — losing a line to a mistimed tap at the card's edge is not a small
/// loss — and focus waits for the entry animation, since a freshly pushed modal has no platform
/// handler for the field on its first pass.
/// </remarks>
public partial class MedicalEntryEditPopupPage : ContentPage
{
    /// <summary>The server's ceiling on one line; also the Editor's <c>MaxLength</c>.</summary>
    public const int MaxLength = 500;

    private const int CounterAppearsWithin = 80;
    private const int FocusDelayMs = 160;

    private readonly TaskCompletionSource<(MedicalEntryKind Kind, string Text)?> _result = new();
    private readonly MedicalEntryKind _originalKind;
    private readonly string _originalText;
    private readonly List<(MedicalEntryKind Kind, SelectChip Chip)> _chips = [];
    private MedicalEntryKind _kind;
    private bool _closing;

    /// <param name="text">The line being changed, or null to add a new one.</param>
    public MedicalEntryEditPopupPage(string? firstName, MedicalEntryKind kind, string? text)
    {
        InitializeComponent();
        // Without OverFullScreen, iOS removes the page underneath and the transparent modal
        // renders over black.
        On<iOS>().SetModalPresentationStyle(UIModalPresentationStyle.OverFullScreen);

        _originalKind = _kind = kind;
        _originalText = text?.Trim() ?? string.Empty;

        var who = string.IsNullOrWhiteSpace(firstName) ? "them" : firstName;
        TitleLabel.Text = text is null ? "Add to the record" : "Change this line";
        DescriptionLabel.Text = text is null
            ? $"One thing a caregiver should know about {who}. Add another for the next."
            : "The old wording stays in the history, so nothing is lost by changing it.";

        TextEditor.MaxLength = MaxLength;
        TextEditor.Text = text;
        BuildKindRow();
        ShowKind();
        UpdateCounter();
    }

    /// <summary>The kind and words saved, or null when cancelled or dismissed.</summary>
    public Task<(MedicalEntryKind Kind, string Text)?> Result => _result.Task;

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        PopupCard.Fit(Card, width);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = Scrim.FadeToAsync(1, 140);
        _ = Card.ScaleToAsync(1, 140, Easing.CubicOut);
        _ = FocusAsync();
    }

    private async Task FocusAsync()
    {
        await Task.Delay(FocusDelayMs);
        if (!_closing)
            TextEditor.Focus();
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

    private async void OnScrimTapped(object? sender, TappedEventArgs e)
    {
        if (!HasEdits())
            await CloseAsync(null);
    }

    private void BuildKindRow()
    {
        for (var i = 0; i < MedicalLedgerLines.Kinds.Count; i++)
        {
            var kind = MedicalLedgerLines.Kinds[i];
            var chip = new SelectChip { Text = MedicalLedgerLines.KindName(kind) };
            chip.Tapped += (_, _) =>
            {
                _kind = kind;
                ShowKind();
            };
            Grid.SetColumn(chip, i % 2);
            Grid.SetRow(chip, i / 2);
            KindRow.Add(chip);
            _chips.Add((kind, chip));
        }
    }

    /// <summary>The set kind filled, the rest outlined, and the box's hint to match.</summary>
    private void ShowKind()
    {
        foreach (var (kind, chip) in _chips)
            chip.IsSelected = kind == _kind;

        TextEditor.Placeholder = MedicalLedgerLines.Placeholder(_kind);
    }

    private void OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        TextError.IsVisible = false;
        UpdateCounter();
    }

    private void UpdateCounter()
    {
        var remaining = MaxLength - (TextEditor.Text?.Length ?? 0);
        CounterLabel.IsVisible = remaining <= CounterAppearsWithin;
        CounterLabel.Text = remaining == 1 ? "1 character left" : $"{remaining} characters left";
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        if (_closing)
            return;

        var text = TextEditor.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            TextError.Text = "Write what should be on file";
            TextError.IsVisible = true;
            return;
        }

        if (text.Length > MaxLength)
        {
            TextError.Text = $"Keep each line under {MaxLength} characters — add another for the rest";
            TextError.IsVisible = true;
            return;
        }

        await CloseAsync((_kind, text));
    }

    private async void OnCancelClicked(object? sender, EventArgs e) => await CloseAsync(null);

    private bool HasEdits() =>
        _kind != _originalKind
        || !string.Equals(TextEditor.Text?.Trim() ?? string.Empty, _originalText, StringComparison.Ordinal);

    private async Task CloseAsync((MedicalEntryKind Kind, string Text)? result)
    {
        if (_closing)
            return;
        _closing = true;

        // Down before the card goes: on Android the modal pops out from under a raised keyboard,
        // which resizes the page underneath mid-animation.
        TextEditor.Unfocus();

        try
        {
            await Task.WhenAll(
                Scrim.FadeToAsync(0, 100),
                Card.ScaleToAsync(0.92, 100, Easing.CubicIn));
            await Navigation.PopModalAsync(animated: false);
        }
        finally
        {
            _result.TrySetResult(result);
        }
    }
}
