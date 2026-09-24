using System.Diagnostics;
using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Infrastructure.Diagnostics;

/// <summary>
/// Tags that say how a member-chat send was answered, stamped on the request's own span.
/// </summary>
/// <remarks>
/// <para>
/// Each model call already names itself: its span carries <see cref="AiTelemetry.ReplySchemaTag"/>
/// (<c>ChatRouteAiResponse</c>, <c>MemberChatClinicalAiResponse</c>, …). What no span said was the
/// decision those calls fed — which workflow answered, whether the router's first choice survived
/// the dispatch rules, and whether a router was consulted at all. That lived only in
/// <c>MemberChatTurns.Workflow</c>, so the clarify rate the routing design turns on
/// (docs/technical/member_chat_routing.md §8) could not be read from Datadog.
/// </para>
/// <para>
/// Privacy: catalogue labels and fixed constants only — never the message, the reply or a name.
/// Same standard as <see cref="AiTelemetry"/>.
/// </para>
/// </remarks>
public static class MemberChatTelemetry
{
    /// <summary>The workflow that answered, as its catalogue label (<c>analysis</c>, <c>steer.casual</c>).</summary>
    public const string WorkflowTag = "chat.workflow";

    /// <summary>The router's first choice, before the dispatch rules (clarify, tie-breaks) acted on it.</summary>
    public const string RoutedTag = "chat.routed";

    /// <summary>The router's runner-up, when it named one.</summary>
    public const string RunnerUpTag = "chat.runner_up";

    /// <summary>What decided the workflow — one of the <c>Source</c> constants below.</summary>
    public const string SourceTag = "chat.route_source";

    /// <summary>A yes or no answering a pending journal offer; settled in code.</summary>
    public const string SourceJournalResume = "journal_resume";

    /// <summary>A message with no question in it; answered in code.</summary>
    public const string SourceNoQuestion = "no_question";

    /// <summary>A yes or no answering a proposed alert-settings change; settled in code.</summary>
    public const string SourcePendingConfirmation = "pending_confirmation";

    /// <summary>The malicious pre-check refused the message; nothing was routed or saved.</summary>
    public const string SourceRefused = "refused";

    /// <summary>The routing call answered.</summary>
    public const string SourceRouter = "router";

    /// <summary>The routing call failed and the pre-check's flags chose the handler.</summary>
    public const string SourceTriageFallback = "triage_fallback";

    /// <summary>
    /// The answer check's verdict on the reply: <c>full</c>, <c>partial</c>, <c>no</c>, or
    /// <c>failed</c> when the check itself did not return. Absent where the check does not run.
    /// </summary>
    public const string AnswerCheckTag = "chat.answer_check";

    /// <summary>Why a reply fell short: <c>not_addressed</c> or <c>not_in_data</c>.</summary>
    public const string AnswerGapTag = "chat.answer_gap";

    public static void TagAnswerCheck(ChatAnswerAssessment assessment)
    {
        var activity = Activity.Current;
        if (activity is null)
            return;

        activity.SetTag(AnswerCheckTag, assessment.Completeness switch
        {
            AnswerCompleteness.Partial => "partial",
            AnswerCompleteness.None => "no",
            _ => "full",
        });

        if (assessment.Cause is { } cause)
        {
            activity.SetTag(AnswerGapTag, cause == AnswerGapCause.NotInData ? "not_in_data" : "not_addressed");
        }
    }

    public static void TagAnswerCheckFailed() =>
        Activity.Current?.SetTag(AnswerCheckTag, "failed");

    public static void TagSource(string source) =>
        Activity.Current?.SetTag(SourceTag, source);

    public static void TagRoute(ChatRouteDecision route)
    {
        var activity = Activity.Current;
        if (activity is null)
            return;

        if (route.Primary is { } primary)
            activity.SetTag(RoutedTag, Label(primary));
        if (route.RunnerUp is { } runnerUp)
            activity.SetTag(RunnerUpTag, Label(runnerUp));
    }

    public static void TagWorkflow(MemberChatWorkflow workflow) =>
        Activity.Current?.SetTag(WorkflowTag, Label(workflow));

    internal static string Label(MemberChatWorkflow workflow) =>
        ChatWorkflowCatalogue.Find(workflow)?.Label ?? workflow.ToString();
}
