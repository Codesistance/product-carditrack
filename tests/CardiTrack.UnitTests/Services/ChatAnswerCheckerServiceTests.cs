using CardiTrack.Application.DTOs.Common;
using CardiTrack.Infrastructure.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The answer check's own contract: what its prompt carries, and how defensively its judgement is
/// read. The examples use invented figures — this repository is public.
/// </summary>
public class ChatAnswerCheckerServiceTests
{
    private static ChatAnswerCheckerService.ChatAnswerCheckAiResponse Response(
        string answered, string? cause = null, string? missing = null) => new()
    {
        Answered = answered,
        Cause = cause,
        Intent = " when in the day he was moving ",
        Missing = missing,
        Reasoning = "Gives totals, not times.",
    };

    [Theory]
    [InlineData("full", null, AnswerCompleteness.Full, null)]
    [InlineData("partial", "notAddressed", AnswerCompleteness.Partial, AnswerGapCause.NotAddressed)]
    [InlineData("no", "not_in_data", AnswerCompleteness.None, AnswerGapCause.NotInData)]
    [InlineData("No", "NotInData", AnswerCompleteness.None, AnswerGapCause.NotInData)]
    [InlineData("partial", "something else", AnswerCompleteness.Partial, null)]
    public void TheJudgement_IsReadDefensively(
        string answered, string? cause, AnswerCompleteness expected, AnswerGapCause? expectedCause)
    {
        var assessment = ChatAnswerCheckerService.ToAssessment(Response(answered, cause, "the times"));

        Assert.Equal(expected, assessment.Completeness);
        Assert.Equal(expectedCause, assessment.Cause);
        Assert.Equal("when in the day he was moving", assessment.Intent);
    }

    /// <summary>An unreadable verdict records nothing rather than a miss that did not happen, and a
    /// full answer carries no cause or gap whatever the model said.</summary>
    [Fact]
    public void AnUnknownVerdict_ReadsAsFull_AndAFullAnswerHasNoGap()
    {
        var assessment = ChatAnswerCheckerService.ToAssessment(Response("maybe", "notAddressed", "the times"));

        Assert.Equal(AnswerCompleteness.Full, assessment.Completeness);
        Assert.Null(assessment.Cause);
        Assert.Null(assessment.Missing);
    }

    [Fact]
    public void ThePrompt_CarriesTheQuestionConversationAndReply_WithWhatTheAppRecords()
    {
        var prompt = ChatAnswerCheckerService.BuildPrompt(
            "when was CardiTrackCardiMember active",
            "--- Earlier in this conversation ---\nCaregiver: how did CardiTrackCardiMember sleep?",
            "The most recent step count I have for CardiTrackCardiMember is 3,000 steps, today so far.");

        Assert.Contains("when was CardiTrackCardiMember active", prompt, StringComparison.Ordinal);
        Assert.Contains("Caregiver: how did CardiTrackCardiMember sleep?", prompt, StringComparison.Ordinal);
        Assert.Contains("--- Reply shown ---", prompt, StringComparison.Ordinal);
        Assert.Contains(ChatAnswerCheckerService.WhatTheAppRecords, prompt, StringComparison.Ordinal);
        Assert.Contains("cannot read hour-by-hour activity or steps here", prompt, StringComparison.Ordinal);
        // All three untrusted sections are framed as text to assess, never as instructions.
        Assert.Contains(MedicalPromptBlocks.ChatMessageGuardrail, prompt, StringComparison.Ordinal);
        // The history holds both sides, so it is framed as the conversation, not as the
        // caregiver's questions — an earlier reply must not pass as caregiver input.
        Assert.Contains(MedicalPromptBlocks.ChatConversationGuardrail, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(MedicalPromptBlocks.ChatHistoryGuardrail, prompt, StringComparison.Ordinal);
        Assert.Contains("\"Reply shown\" is the app's own reply", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void AFirstMessage_CarriesNoHistorySectionOrItsGuardrail()
    {
        var prompt = ChatAnswerCheckerService.BuildPrompt("how did he sleep", null, "About 6h 10m last night.");

        Assert.DoesNotContain(MedicalPromptBlocks.ChatConversationGuardrail, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAssessment_RoundTripsThroughItsStoredForm_AndGarbageReadsAsNothing()
    {
        var assessment = new ChatAnswerAssessment
        {
            Completeness = AnswerCompleteness.Partial,
            Cause = AnswerGapCause.NotInData,
            Intent = "when he was active",
            Missing = "times of day",
            Reasoning = "Daily totals only.",
        };

        Assert.Equal(assessment, ChatAnswerAssessment.FromJson(assessment.ToJson()));
        Assert.Null(ChatAnswerAssessment.FromJson("not json"));
        Assert.Null(ChatAnswerAssessment.FromJson(null));
    }
}
