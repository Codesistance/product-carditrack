namespace CardiTrack.Mobile.Controls;

/// <summary>
/// A caregiver's own question about this CardiMember, answered from the readings on file.
/// </summary>
/// <remarks>
/// The page owns the API call. This control collects the question, shows busy/error/answer,
/// and keeps the last exchange on screen until the member changes. Nothing is stored server-side.
/// </remarks>
public partial class AskCard : ContentView
{
    public AskCard()
    {
        InitializeComponent();
    }

    /// <summary>The caregiver asked a question. Carries the trimmed text.</summary>
    public event EventHandler<string>? QuestionSubmitted;

    /// <summary>Puts the member's first name in the heading and the placeholder.</summary>
    public void SetMember(string? firstName)
    {
        var name = string.IsNullOrWhiteSpace(firstName) ? "them" : firstName;
        TitleLabel.Text = $"Ask about {name}";
        QuestionEditor.Placeholder = $"How did {name} sleep last night?";
    }

    /// <summary>Locks the editor while a model call is in flight.</summary>
    public void SetBusy(bool busy)
    {
        QuestionEditor.IsReadOnly = busy;
        AskButton.IsEnabled = !busy && HasQuestion(QuestionEditor.Text);
        AskButton.Text = busy ? "Thinking…" : "Ask";
        BusyIndicator.IsVisible = busy;
        BusyIndicator.IsRunning = busy;
        if (busy)
            ErrorLabel.IsVisible = false;
    }

    /// <summary>Shows the latest exchange. The question box is cleared so a follow-up is a new ask.</summary>
    public void ShowAnswer(string question, string answer)
    {
        AskedLabel.Text = question;
        AnswerLabel.Text = answer;
        AnswerCard.IsVisible = true;
        ErrorLabel.IsVisible = false;
        QuestionEditor.Text = string.Empty;
        AskButton.IsEnabled = false;
    }

    public void ShowError(string message)
    {
        ErrorLabel.Text = message;
        ErrorLabel.IsVisible = true;
    }

    /// <summary>Clears the last exchange when the page switches member.</summary>
    public void Reset()
    {
        QuestionEditor.Text = string.Empty;
        AnswerCard.IsVisible = false;
        ErrorLabel.IsVisible = false;
        SetBusy(false);
        AskButton.IsEnabled = false;
    }

    private void OnQuestionChanged(object? sender, TextChangedEventArgs e) =>
        AskButton.IsEnabled = !QuestionEditor.IsReadOnly && HasQuestion(e.NewTextValue);

    private void OnAskClicked(object? sender, EventArgs e)
    {
        var question = QuestionEditor.Text?.Trim();
        if (!HasQuestion(question))
            return;

        QuestionSubmitted?.Invoke(this, question!);
    }

    private static bool HasQuestion(string? text) =>
        !string.IsNullOrWhiteSpace(text);
}
