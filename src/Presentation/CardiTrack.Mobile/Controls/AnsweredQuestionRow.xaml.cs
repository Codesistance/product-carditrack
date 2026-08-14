using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Mobile.Controls;

/// <summary>An answered question, its answerer's caption, and who to show it as. What binds
/// <see cref="AnsweredQuestionRow"/> to a <see cref="QuestionnaireResponse"/> in the archive
/// CollectionView — the member's name lives on the page, not the API response, so it travels
/// alongside the questionnaire rather than being looked up again per row.</summary>
public sealed record AnsweredQuestionnaireItem(QuestionnaireResponse Questionnaire, string? MemberName);

/// <summary>
/// One row in the Q&amp;A archive's lazy-loaded list — a <see cref="QuestionCard"/> in its
/// answered mode, including the in-card delete affordance.
/// </summary>
/// <remarks>
/// A CollectionView template recycles this instance across items as the list scrolls, so state is
/// read from <c>BindingContext</c> at event time rather than captured in a closure — the
/// same instance that raised the click may already have moved on to a different item's data by the
/// time a fire-and-forget save completes.
/// </remarks>
public partial class AnsweredQuestionRow : ContentView
{
    /// <summary>The caregiver saved an answer edit. Carries the questionnaire it was for and the
    /// trimmed text, since <c>BindingContext</c> may have moved on by the time a handler
    /// reacts to this.</summary>
    public event EventHandler<(QuestionnaireResponse Questionnaire, string Answer)>? AnswerSubmitted;

    /// <summary>The caregiver asked to delete this question and its answer.</summary>
    public event EventHandler<QuestionnaireResponse>? DeleteRequested;

    private AnsweredQuestionnaireItem? _item;

    public AnsweredQuestionRow()
    {
        InitializeComponent();

        Card.AnswerSubmitted += (_, answer) =>
        {
            if (_item is not null)
                AnswerSubmitted?.Invoke(this, (_item.Questionnaire, answer));
        };
        Card.DeleteRequested += (_, _) =>
        {
            if (_item is not null)
                DeleteRequested?.Invoke(this, _item.Questionnaire);
        };
    }

    /// <summary>Locks the row's card while a save or delete for it is in flight.</summary>
    public void SetBusy(bool busy) => Card.SetBusy(busy);

    /// <summary>Closes the answer editor after a save the page has already applied.</summary>
    public void CloseEditor() => Card.CloseEditor();

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();

        _item = BindingContext as AnsweredQuestionnaireItem;
        if (_item is null)
            return;

        Card.Apply(_item.Questionnaire, _item.MemberName);
    }
}
