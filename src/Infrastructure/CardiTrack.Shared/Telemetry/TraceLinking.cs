using System.Diagnostics;

namespace CardiTrack.Shared.Telemetry;

/// <summary>
/// Parses a stored W3C traceparent back into an <see cref="ActivityLink"/>, for stitching a span
/// to a trace it isn't a child of — the cross-process/cross-time cases where a real shared trace
/// id isn't reliable (webhook-receiver publish → pipeline-jobs drain; a push send → its ack,
/// which can arrive independently up to the escalation ladder's 900s ceiling later).
/// </summary>
public static class TraceLinking
{
    /// <summary>Degrades to an empty list on null/unparseable input — no throw, an unlinked span.</summary>
    public static IEnumerable<ActivityLink> BuildLinks(string? traceParent)
    {
        if (traceParent is not null && ActivityContext.TryParse(traceParent, null, out var context))
            yield return new ActivityLink(context);
    }

    /// <summary>Single-link convenience for call sites adding a link to an already-started Activity.</summary>
    public static ActivityLink? TryBuildLink(string? traceParent) =>
        traceParent is not null && ActivityContext.TryParse(traceParent, null, out var context)
            ? new ActivityLink(context)
            : null;
}
