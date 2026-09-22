using System.Diagnostics;
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
}
