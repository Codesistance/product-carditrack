using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Questionnaires;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// One question the service is asking a family, with the answer box it opens into.
/// </summary>
/// <remarks>
/// Two modes, one control. On the member's page it shows a pending question with an "Answer"
/// button; on the questions page it shows an answered one with the answer prefilled and "Edit
/// answer" instead. The difference is entirely in <see cref="Apply"/> — the editor, the animation
/// and the save path are the same in both, which is what keeps editing an answer feeling like
/// giving one.
/// </remarks>
public partial class QuestionCard : ContentView
{
    private const uint DropdownMs = 200;
    private const string DropdownAnimation = "questionEditor";

    private bool _isOpen;
    private string? _originalAnswer;

    public QuestionCard()
    {
        InitializeComponent();
    }

    /// <summary>The caregiver typed an answer and asked to save it. Carries the trimmed text.</summary>
    public event EventHandler<string>? AnswerSubmitted;

    /// <summary>The caregiver skipped the question.</summary>
    public event EventHandler? DismissRequested;

    /// <summary>The caregiver asked to delete this answered question and its answer.</summary>
    public event EventHandler? DeleteRequested;

    /// <summary>The questionnaire this card is showing.</summary>
    public QuestionnaireResponse? Questionnaire { get; private set; }

    /// <summary>
    /// Whether the answer box is open. The pages check this before re-rendering on a silent
    /// refresh: a background reload that closed an editor mid-sentence would lose what was typed.
    /// </summary>
    public bool IsEditing => _isOpen;

    /// <summary>Shows a question. Pass a member's first name so the heading can use it.</summary>
    public void Apply(QuestionnaireResponse questionnaire, string? memberFirstName)
    {
        Questionnaire = questionnaire;
        _originalAnswer = questionnaire.AnswerText;

        var isAnswered = MemberQuestionnaires.IsAnswerable(questionnaire.AnswerText);
        var name = string.IsNullOrWhiteSpace(memberFirstName) ? "them" : memberFirstName;

        TitleLabel.Text = isAnswered ? "You answered" : $"A quick question about {name}";
        QuestionLabel.Text = questionnaire.QuestionText;

        RationaleCard.IsVisible = !string.IsNullOrWhiteSpace(questionnaire.TriggerContext);
        RationaleLabel.Text = questionnaire.TriggerContext;

        OptionalLabel.Text = MemberQuestionnaires.Softener(questionnaire.Scope, isAnswered);

        OpenButton.Text = isAnswered ? "Edit answer" : "Answer";
        DismissButton.IsVisible = !isAnswered;
        DeleteButton.IsVisible = isAnswered;

        CaptionLabel.IsVisible = isAnswered;
        if (isAnswered)
        {
            var answeredAt = questionnaire.AnsweredAtUtc ?? questionnaire.GeneratedAtUtc;
            CaptionLabel.Text = $"Answered {RelativeTime.Format(answeredAt)}";
        }

        AnswerEditor.Text = questionnaire.AnswerText ?? string.Empty;
        SaveButton.IsEnabled = MemberQuestionnaires.IsAnswerable(AnswerEditor.Text);

        CloseEditor(animated: false);
    }

    /// <summary>Locks the card while a save, skip, or delete is in flight.</summary>
    public void SetBusy(bool busy)
    {
        SaveButton.IsEnabled = !busy && MemberQuestionnaires.IsAnswerable(AnswerEditor.Text);
        CancelButton.IsEnabled = !busy;
        OpenButton.IsEnabled = !busy;
        DismissButton.IsEnabled = !busy;
        DeleteButton.IsEnabled = !busy;
        DeleteButton.InputTransparent = busy;
        AnswerEditor.IsReadOnly = busy;
    }

    /// <summary>
    /// Shuts the editor after a save the page handled — the answer is stored, so the box that
    /// collected it has nothing left to do.
    /// </summary>
    public void CloseEditor() => CloseEditor(animated: true);

    private void OnOpenClicked(object? sender, EventArgs e)
    {
        if (_isOpen)
            CloseEditor(animated: true);
        else
            OpenEditor();
    }

    private void OnAnswerChanged(object? sender, TextChangedEventArgs e) =>
        SaveButton.IsEnabled = MemberQuestionnaires.IsAnswerable(e.NewTextValue);

    private void OnSaveClicked(object? sender, EventArgs e)
    {
        var answer = AnswerEditor.Text?.Trim();
        if (!MemberQuestionnaires.IsAnswerable(answer))
            return;

        AnswerSubmitted?.Invoke(this, answer!);
    }

    private void OnCancelClicked(object? sender, EventArgs e)
    {
        // Back to what was stored, not to empty: cancelling an edit means keeping the answer that
        // was already there.
        AnswerEditor.Text = _originalAnswer ?? string.Empty;
        CloseEditor(animated: true);
    }

    private void OnDismissClicked(object? sender, EventArgs e) =>
        DismissRequested?.Invoke(this, EventArgs.Empty);

    private void OnDeleteTapped(object? sender, TappedEventArgs e) =>
        DeleteRequested?.Invoke(this, EventArgs.Empty);

    private void OpenEditor()
    {
        _isOpen = true;

        // The card's own width, or the panel's measured natural width before layout has given it
        // one — a first open on a page still being laid out must not measure against zero.
        var width = Width > 0 ? Width : double.PositiveInfinity;
        var target = EditorPanel.Measure(width, double.PositiveInfinity).Height;

        this.AbortAnimation(DropdownAnimation);
        new Animation(v => EditorClip.HeightRequest = v, EditorClip.Height, target)
            .Commit(this, DropdownAnimation, 16, DropdownMs, Easing.CubicOut,
                (_, _) => AnswerEditor.Focus());
    }

    private void CloseEditor(bool animated)
    {
        _isOpen = false;
        this.AbortAnimation(DropdownAnimation);

        if (!animated)
        {
            EditorClip.HeightRequest = 0;
            return;
        }

        new Animation(v => EditorClip.HeightRequest = v, EditorClip.Height, 0)
            .Commit(this, DropdownAnimation, 16, DropdownMs, Easing.CubicIn);
    }
}
