using System.Diagnostics;
using System.Diagnostics.Metrics;
using CardiTrack.Shared.Telemetry;

namespace CardiTrack.Infrastructure.Diagnostics;

/// <summary>
/// Spans for the per-member work inside a pipeline pass — the layer between the job's root span
/// (<c>PipelineJobs.Program</c>) and the AI and Npgsql spans the clients already emit. Shares
/// <see cref="TelemetryNames.PipelineSource"/> with <c>PipelineJobs.PipelineTelemetry</c>, the
/// same two-instances-one-name arrangement <see cref="PushTelemetry"/> has with Application's
/// <c>PushDispatchTelemetry</c>: OpenTelemetry subscribes by name, so the project that emits and
/// the project that registers need not reference each other.
/// </summary>
/// <remarks>
/// <para>
/// <b>No member identifier is recorded here, deliberately.</b> The push spine's privacy invariant
/// tags a delivery id and never the person it belongs to, and this follows it. A CardiMember id on
/// a span, next to the rules that fired and the readings the AI spans carry, is health data in a
/// second signal — and it would buy very little, because the join already exists in the other
/// direction: every log line inside this span carries both <c>CardiMemberId</c> and, now that the
/// job has a root span at all, the <c>trace_id</c>. Find the member in Logs, open their trace from
/// the line. What goes on the span is what is true of the pass rather than of the person: how many
/// findings the rules produced, how many survived suppression, how many were raised.
/// </para>
/// <para>
/// Rule names are recorded, because a rule is a property of the engine and not of anyone.
/// </para>
/// </remarks>
public static class JudgementTelemetry
{
    public static readonly ActivitySource Source = new(TelemetryNames.PipelineSource);

    /// <summary>Findings the rules produced, before cooldown and dedup.</summary>
    public const string FindingsTag = "judgement.findings";

    /// <summary>Findings that survived suppression and were sent to the model.</summary>
    public const string JudgedTag = "judgement.judged";

    /// <summary>Alerts written from the verdicts.</summary>
    public const string RaisedTag = "judgement.raised";

    /// <summary>The rules that produced a finding this pass, comma-separated.</summary>
    public const string RulesTag = "judgement.rules";

    public static readonly Meter Meter = new(TelemetryNames.PipelineSource);

    /// <summary>
    /// What happened to each verdict, by <see cref="OutcomeTag"/> and rule. One counter with an
    /// outcome dimension rather than one counter per exit: the question asked of it is always a
    /// proportion — how much of what the rules found is reaching a family — and five separate
    /// series cannot be divided by each other on a dashboard without naming all five every time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The four fail-closed exits were, until this counter, observable only as Warning log lines.
    /// That is enough to diagnose one once you already suspect it and nowhere near enough to
    /// notice one: <c>activity_decline</c> lost every verdict it was given for at least an hour on
    /// 2026-09-22 at a five-minute cadence, and nothing reported it — a warning that recurs on a
    /// schedule reads as background, and in prod, where the root log level is Warning, the
    /// successful exits do not log at all, so there was not even a denominator to compare against.
    /// </para>
    /// <para>
    /// Cardinality is bounded and small: five outcomes by eleven rules. No member identifier, and
    /// no model output — the same standard <see cref="AiTelemetry"/> and <see cref="PushTelemetry"/>
    /// hold to.
    /// </para>
    /// </remarks>
    public static readonly Counter<long> Verdicts = Meter.CreateCounter<long>(
        "carditrack.judgement.verdict",
        description: "Verdicts returned for a finding, by rule and what became of them");

    /// <summary>Which exit a verdict took — one of the <c>Outcome</c> constants below.</summary>
    public const string OutcomeTag = "judgement.outcome";

    /// <summary>The finding's rule. A rule is a property of the engine, not of a person.</summary>
    public const string RuleTag = "judgement.rule";

    /// <summary>No verdict carried this finding's rule. Fails closed; re-judged next pass.</summary>
    public const string OutcomeUnmatched = "unmatched";

    /// <summary>The severity word was outside the taxonomy. Fails closed.</summary>
    public const string OutcomeSeverityUnmapped = "severity_unmapped";

    /// <summary>Judged below Yellow — not worth the family's attention today. Not a failure.</summary>
    public const string OutcomeBenign = "benign";

    /// <summary>The copy failed the caregiver-register guard. Fails closed.</summary>
    public const string OutcomeMessageRejected = "message_rejected";

    /// <summary>An alert was written.</summary>
    public const string OutcomeRaised = "raised";
}
