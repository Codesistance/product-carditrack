using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Mobile.Core.Alerts;

/// <summary>
/// The line under the summary card's urgency rung on the day it and the dashboard hero disagree
/// — a yellow "Something's different" on the dashboard sitting over a green "Nothing pressing
/// today" on Details, with nothing between the two screens saying why.
/// </summary>
/// <remarks>
/// <para>
/// Two independent writers, by design. The hero's colour is <c>HealthStatus</c>, which
/// <c>MemberInsightsCalculator.ComputeHealthStatus</c> takes from the worst unresolved alert at
/// yellow or above and from nothing else. The rung is <see cref="DigestResponse.Urgency"/>, the
/// model's own read of the day's readings, which never drives an alert row and is never driven
/// by one (<c>DigestUrgency</c>). They share the 1–4 scale — green/watch, yellow/check-in,
/// orange/concerning, red/act-now — which is what makes them comparable here without a mapping
/// table, and also what makes a disagreement read as a contradiction rather than as two answers
/// to two different questions.
/// </para>
/// <para>
/// So the note names the writer, not the tier rule. When the hero sits above the rung the reason
/// is always an open alert — including an acknowledged one, which the dashboard's Recent Alerts
/// strip has stopped showing but the colour still reads (<c>DashboardService</c>). That is the
/// case a caregiver cannot reconstruct from the screens in front of them: a yellow hero over an
/// empty strip, and a summary calling the day quiet. It is deliberately not "the hour-by-hour
/// check is saying something different": the assessment raises the tier the status *sentence* is
/// written against (<c>StatusDisplayTier</c>), never the colour, and blaming it here would pin
/// the colour on a writer that cannot produce it.
/// </para>
/// <para>
/// Compared on the client, from fields both screens already carry, rather than from a source
/// column on the persisted status line: that row records a sentence, not the tier it was
/// written under, and the colour the caregiver is reconciling against is not that row's at all.
/// Lives here rather than in the page for the reason <see cref="ReassuranceCopy"/> does — it is
/// wording a family reads about a relative, and the rule for when it appears is worth a test.
/// </para>
/// </remarks>
public static class StatusAgreement
{
    /// <summary>
    /// The reconciling line, or null when there is nothing to reconcile: the two agree, the rung
    /// is the louder one (the digest asking for more than the alerts do is not this note's case
    /// — the hero's sentence already takes that tier), the hero has no colour to be above anything
    /// (unknown, or monitoring paused), or either side is missing.
    /// </summary>
    public static string? Note(CardiMemberDetailResponse member, DigestResponse digest)
    {
        // A paused member's hero says "Monitoring paused", which is not a colour. HealthStatus is
        // still computed for them, so the guard has to be explicit rather than falling out of the
        // rank lookup below.
        if (member.MonitoringPaused)
            return null;

        if (HeroRank(member.HealthStatus) is not { } hero || RungRank(digest.Urgency) is not { } rung)
            return null;

        if (hero <= rung)
            return null;

        // The colour word, not the hero's headline: the headline is the card's own copy and
        // changes per tier, while the colour is what the caregiver actually carried over from
        // the dashboard. "Still open" rather than "unresolved" — resolution is a lifecycle word
        // the app never shows a family.
        return $"The dashboard is showing {member.HealthStatus} because an alert is still open. "
               + "This summary only reads today's numbers — Alerts has what's still standing.";
    }

    /// <summary>
    /// <c>HealthStatus</c> on the shared scale. "unknown" (no baseline yet — the hero shows the
    /// day's own readings) and "paused" rank nowhere, as does anything unrecognised: a value this
    /// client has not heard of is not a reason to tell a family the dashboard is louder.
    /// </summary>
    private static int? HeroRank(string? healthStatus) => healthStatus switch
    {
        "green" => 1,
        "yellow" => 2,
        "orange" => 3,
        "red" => 4,
        _ => null,
    };

    /// <summary>The digest's wire vocabulary on the same scale — the words <c>DigestQueryService</c> sends.</summary>
    private static int? RungRank(string? urgency) => urgency switch
    {
        "watch" => 1,
        "check-in" => 2,
        "concerning" => 3,
        "act-now" => 4,
        _ => null,
    };
}
