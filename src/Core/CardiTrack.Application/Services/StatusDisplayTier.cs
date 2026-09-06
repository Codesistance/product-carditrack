using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <summary>
/// The dashboard hero's severity tier: the worst of unresolved alerts, a recent yellow-or-worse
/// hour assessment, and today's family digest above Watch. Alert rows and digest urgency stay
/// independent writers — this is display only.
/// </summary>
/// <remarks>
/// <see cref="AlertSeverity"/> and <see cref="DigestUrgency"/> share the same 1–4 scale
/// (Green/Watch, Yellow/CheckIn, Orange/Concerning, Red/ActNow), which is what makes the digest
/// comparable without a mapping table. Yellow assessments do not raise Alert rows, so leaving
/// them out of this max left the hero saying "settled" on a day the digest asked for a check-in.
/// </remarks>
public static class StatusDisplayTier
{
    /// <summary>
    /// How recent an hour assessment may be to still colour the hero. Window start, not generation
    /// time: a stale hour is yesterday's picture even if the row was written later.
    /// </summary>
    public static readonly TimeSpan AssessmentFreshness = TimeSpan.FromHours(24);

    public static AlertSeverity Resolve(
        AlertSeverity highestUnresolvedAlert,
        RealtimeAssessment? latestAssessment,
        DigestEntry? latestFamilyDigest,
        DateTime utcNow)
    {
        var tier = highestUnresolvedAlert;

        if (latestAssessment is { Severity: { } assessmentSeverity }
            && assessmentSeverity >= AlertSeverity.Yellow
            && utcNow - latestAssessment.WindowStartUtc < AssessmentFreshness
            && assessmentSeverity > tier)
        {
            tier = assessmentSeverity;
        }

        if (latestFamilyDigest?.Urgency is { } urgency && urgency > DigestUrgency.Watch)
        {
            var digestAsAlert = (AlertSeverity)(int)urgency;
            if (digestAsAlert > tier)
                tier = digestAsAlert;
        }

        return tier;
    }
}
