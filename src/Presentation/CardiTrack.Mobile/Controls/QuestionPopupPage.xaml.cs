using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Questionnaires;
using CardiTrack.Mobile.Services;
using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// The CardiMember card's pending question, opened as a modal — what the Dashboard's Q&amp;A icon
/// taps into. Same shell and card as <see cref="AppPopupPage"/> and <see cref="WeatherPopupPage"/>,
/// with the question, an answer box and the popups' Cancel / Save pair as its body.
/// </summary>
/// <remarks>
/// Its own layout rather than a <see cref="QuestionCard"/>: that card is built to sit on a page
/// (see the XAML for what it looked like set in a popup), and it stays as it is for the member
/// page. The words come from the same place the card takes them — the title the card gives a
/// pending question, <see cref="MemberQuestionnaires"/> for the softener and what counts as an
/// answer — so the two cannot drift into asking one question two ways.
/// </remarks>
public partial class QuestionPopupPage : ContentPage
{
    private readonly IPopupService _popups;
    private readonly string _originalAnswer;

    // RunContinuationsAsynchronously, same reason as WeatherPopupPage: completing this from
    // CloseAsync/OnDisappearing (both already on the UI thread) must not run the awaiter's
    // continuation in-line.
    private readonly TaskCompletionSource<QuestionPopupResult> _closed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _closing;

    public QuestionPopupPage(QuestionnaireResponse pending, string? memberFirstName, IPopupService popups)
    {
        InitializeComponent();
        // Without OverFullScreen, iOS removes the page underneath and the transparent modal
        // renders over black.
        On<iOS>().SetModalPresentationStyle(UIModalPresentationStyle.OverFullScreen);

        _popups = popups;

        // The info badge's tint, as AppPopupPage mixes it.
        IconBadge.BackgroundColor = ControlResources.Color("Primary", Colors.SteelBlue).WithAlpha(0.14f);

        var name = string.IsNullOrWhiteSpace(memberFirstName) ? "them" : memberFirstName;
        TitleLabel.Text = $"A quick question about {name}";
        QuestionLabel.Text = pending.QuestionText;
        RationaleCard.IsVisible = !string.IsNullOrWhiteSpace(pending.TriggerContext);
        RationaleLabel.Text = pending.TriggerContext;
        OptionalLabel.Text = MemberQuestionnaires.Softener(pending.Scope, isAnswered: false);

        _originalAnswer = pending.AnswerText ?? string.Empty;
        AnswerEditor.Text = _originalAnswer;
        SaveButton.IsEnabled = MemberQuestionnaires.IsAnswerable(AnswerEditor.Text);
    }

    /// <summary>Completes with what the caregiver did, however the popup closed.</summary>
    public Task<QuestionPopupResult> Closed => _closed.Task;

    /// <summary>Same width rule as the other popups; see <see cref="PopupCard"/>.</summary>
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
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (!_closing && !Navigation.ModalStack.Contains(this))
            _closed.TrySetResult(QuestionPopupResult.Cancelled);
    }

    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync(QuestionPopupResult.Cancelled);
        return true;
    }

    /// <summary>
    /// Tapping away closes only while nothing has been typed — the rule the other editors follow:
    /// an answer lost to a mistimed tap at the card's edge is not a small loss.
    /// </summary>
    private async void OnScrimTapped(object? sender, TappedEventArgs e)
    {
        if (string.Equals(AnswerEditor.Text ?? string.Empty, _originalAnswer, StringComparison.Ordinal))
            await CloseAsync(QuestionPopupResult.Cancelled);
    }

    private void OnAnswerChanged(object? sender, TextChangedEventArgs e) =>
        SaveButton.IsEnabled = MemberQuestionnaires.IsAnswerable(e.NewTextValue);

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        var answer = AnswerEditor.Text?.Trim();
        if (!MemberQuestionnaires.IsAnswerable(answer))
            return;

        await CloseAsync(new QuestionPopupResult(QuestionPopupOutcome.Answered, answer));
    }

    private async void OnCancelClicked(object? sender, EventArgs e) =>
        await CloseAsync(QuestionPopupResult.Cancelled);

    private async void OnDismissClicked(object? sender, EventArgs e)
    {
        // Same confirmation weight and wording QuestionnairesPage/CardiMemberDetailPage already
        // use for skipping — this popup is a third place that can raise it, not a second wording.
        var confirmed = await _popups.ConfirmInfoAsync(
            "We won't ask this one again.", "Skip this question?", "Yes, skip", "Keep it");
        if (confirmed)
            await CloseAsync(new QuestionPopupResult(QuestionPopupOutcome.Dismissed));
    }

    private async Task CloseAsync(QuestionPopupResult result)
    {
        if (_closing)
            return;
        _closing = true;

        // Down before the card goes: on Android the modal pops out from under a raised keyboard,
        // which resizes the page underneath mid-animation.
        AnswerEditor.Unfocus();

        try
        {
            await Task.WhenAll(
                Scrim.FadeToAsync(0, 100),
                Card.ScaleToAsync(0.92, 100, Easing.CubicIn));
            await Navigation.PopModalAsync(animated: false);
        }
        finally
        {
            _closed.TrySetResult(result);
        }
    }
}
