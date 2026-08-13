using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Controls;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Questionnaires;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

/// <summary>
/// Everything the service has asked this member's family, and what they answered — with the answers
/// editable and removable.
/// </summary>
/// <remarks>
/// An archive rather than a live screen, so it refetches when opened and on a pull, and does not
/// poll: nothing here changes unless the person looking at it changes it.
/// </remarks>
[QueryProperty(nameof(MemberId), "memberId")]
[QueryProperty(nameof(MemberName), "name")]
public partial class QuestionnairesPage : ContentPage
{
    /// <summary>Shell route; see <see cref="AppShell"/>.</summary>
    public const string Route = "memberquestions";

    private readonly ICardiTrackApiClient _api;
    private readonly IPopupService _popups;

    private Guid _memberId;
    private string? _memberName;
    private bool _isBusy;

    public QuestionnairesPage(ICardiTrackApiClient api, IPopupService popups)
    {
        InitializeComponent();
        _api = api;
        _popups = popups;

        PendingCard.AnswerSubmitted += OnPendingAnswered;
        PendingCard.DismissRequested += OnPendingDismissed;
    }

    public string MemberId
    {
        set => _memberId = Guid.TryParse(Uri.UnescapeDataString(value ?? string.Empty), out var id)
            ? id
            : Guid.Empty;
    }

    /// <summary>
    /// Passed through from the page that opened this one rather than refetched. The name is only
    /// wanted for the copy, and the caller already has it — a second round trip to address someone
    /// by name would be a slower screen for nothing.
    /// </summary>
    public string MemberName
    {
        set => _memberName = Uri.UnescapeDataString(value ?? string.Empty);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = LoadAsync();
    }

    private async void OnPullToRefresh(object? sender, EventArgs e)
    {
        await LoadAsync();
        Refresher.IsRefreshing = false;
    }

    private void OnRetryClicked(object? sender, EventArgs e) => _ = LoadAsync();

    private async void OnBackTapped(object? sender, EventArgs e) =>
        await this.GoBackAsync($"{CardiMemberDetailPage.Route}?memberId={_memberId}");

    private async Task LoadAsync()
    {
        SetState(loading: true);

        try
        {
            var questionnaires = await _api.GetQuestionnairesAsync(_memberId);
            Apply(questionnaires);
            SetState(loaded: true);
        }
        catch (ApiException ex)
        {
            ErrorDetailLabel.Text = ex.Message;
            SetState(error: true);
        }
    }

    private void Apply(IReadOnlyList<QuestionnaireResponse> questionnaires)
    {
        var name = string.IsNullOrWhiteSpace(_memberName) ? "them" : _memberName;
        IntroLabel.Text =
            $"Things we've asked about {name}, and what you told us. Your answers help us read "
            + "their readings properly.";

        var pending = MemberQuestionnaires.FirstPending(questionnaires);
        PendingCard.IsVisible = pending is not null;
        if (pending is not null)
            PendingCard.Apply(pending, _memberName);

        var answered = MemberQuestionnaires.AnsweredNewestFirst(questionnaires);

        AnsweredStack.Clear();
        foreach (var questionnaire in answered)
            AnsweredStack.Add(BuildAnsweredCard(questionnaire));

        EmptyPanel.IsVisible = answered.Count == 0;
        EmptyDetailLabel.Text =
            $"When we ask something about {name} and you answer, we'll keep it here.";
    }

    /// <summary>
    /// One answered question. The card is the same control the pending question uses, in its
    /// prefilled mode — so editing an answer is the same gesture as giving one — with the edit and
    /// delete affordances beneath it.
    /// </summary>
    private View BuildAnsweredCard(QuestionnaireResponse questionnaire)
    {
        var card = new QuestionCard();
        card.Apply(questionnaire, _memberName);
        card.AnswerSubmitted += async (_, answer) => await SaveAnswerAsync(card, questionnaire, answer);

        var answeredAt = questionnaire.AnsweredAtUtc ?? questionnaire.GeneratedAtUtc;
        var caption = new Label
        {
            Text = $"Answered {RelativeTime.Format(answeredAt)}",
            FontFamily = "Quicksand",
            FontSize = 12,
            TextColor = (Color)App.Current!.Resources["BodyText"],
            VerticalTextAlignment = TextAlignment.Center,
        };

        var delete = new ImageButton
        {
            Source = "icon_trash.svg",
            BackgroundColor = Colors.Transparent,
            WidthRequest = 44,
            HeightRequest = 44,
            Padding = 11,
        };
        SemanticProperties.SetDescription(delete, "Delete this question and answer");
        delete.Clicked += async (_, _) => await DeleteAsync(questionnaire);

        var footer = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
            ColumnSpacing = 8,
            Margin = new Thickness(4, 0, 0, 0),
        };
        footer.Add(caption);
        footer.Add(delete, column: 1);

        var layout = new VerticalStackLayout { Spacing = 4 };
        layout.Add(card);
        layout.Add(footer);
        return layout;
    }

    private async Task SaveAnswerAsync(QuestionCard card, QuestionnaireResponse questionnaire, string answer)
    {
        if (_isBusy)
            return;

        _isBusy = true;
        card.SetBusy(true);
        try
        {
            await _api.AnswerQuestionnaireAsync(
                questionnaire.Id, new AnswerQuestionnaireRequest { AnswerText = answer });

            // Reloaded rather than patched in place: an edit can change what the pending card
            // shows too, and this page is cheap to rebuild.
            card.CloseEditor();
            await LoadAsync();
        }
        catch (ApiException ex) when (!ex.IsSessionExpired)
        {
            // Editor stays open, text intact — retrying must not mean retyping.
            await _popups.ShowWarningAsync(ex.Message, "Couldn't save your answer");
        }
        finally
        {
            _isBusy = false;
            card.SetBusy(false);
        }
    }

    private async Task DeleteAsync(QuestionnaireResponse questionnaire)
    {
        if (_isBusy)
            return;

        // There is no undo, and the answer is gone for the whole family — the same confirmation
        // weight deleting an alert carries.
        var confirmed = await _popups.ConfirmWarningAsync(
            "This question and your answer will be removed for everyone.",
            "Delete this answer?",
            "Yes, delete");
        if (!confirmed)
            return;

        _isBusy = true;
        try
        {
            await _api.DeleteQuestionnaireAsync(questionnaire.Id);
            await LoadAsync();
        }
        catch (ApiException ex) when (!ex.IsSessionExpired)
        {
            await _popups.ShowErrorAsync(ex.Message, "Couldn't delete that answer");
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async void OnPendingAnswered(object? sender, string answer)
    {
        if (PendingCard.Questionnaire is { } questionnaire)
            await SaveAnswerAsync(PendingCard, questionnaire, answer);
    }

    private async void OnPendingDismissed(object? sender, EventArgs e)
    {
        if (PendingCard.Questionnaire is not { } questionnaire || _isBusy)
            return;

        var confirmed = await _popups.ConfirmInfoAsync(
            "We won't ask this one again.", "Skip this question?", "Yes, skip", "Keep it");
        if (!confirmed)
            return;

        _isBusy = true;
        PendingCard.SetBusy(true);
        try
        {
            await _api.DismissQuestionnaireAsync(questionnaire.Id);
            await LoadAsync();
        }
        catch (ApiException ex) when (!ex.IsSessionExpired)
        {
            await _popups.ShowWarningAsync(ex.Message, "Couldn't skip that question");
        }
        finally
        {
            _isBusy = false;
            PendingCard.SetBusy(false);
        }
    }

    private void SetState(bool loading = false, bool loaded = false, bool error = false)
    {
        SkeletonPanel.IsVisible = loading;
        ContentPanel.IsVisible = loaded;
        ErrorPanel.IsVisible = error;
    }
}
