using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Services;
using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// The CardiMember card's pending question, opened as a modal — what the Dashboard's Q&amp;A icon
/// taps into. Same shell as <see cref="WeatherPopupPage"/>, with a <see cref="QuestionCard"/> as
/// the body instead of a read-only detail table.
/// </summary>
public partial class QuestionPopupPage : ContentPage
{
    private readonly IPopupService _popups;

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
        Question.Apply(pending, memberFirstName);
        Question.AnswerSubmitted += OnAnswerSubmitted;
        Question.DismissRequested += OnDismissRequested;
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

    private async void OnScrimTapped(object? sender, TappedEventArgs e) =>
        await CloseAsync(QuestionPopupResult.Cancelled);

    private async void OnAnswerSubmitted(object? sender, string answer) =>
        await CloseAsync(new QuestionPopupResult(QuestionPopupOutcome.Answered, answer));

    private async void OnDismissRequested(object? sender, EventArgs e)
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
