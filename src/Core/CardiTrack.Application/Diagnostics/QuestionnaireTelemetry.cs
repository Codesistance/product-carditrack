using System.Diagnostics.Metrics;
using CardiTrack.Domain.Enums;
using CardiTrack.Shared.Telemetry;

namespace CardiTrack.Application.Diagnostics;

/// <summary>
/// Product counters for the family-questionnaire loop: did we ask, what did the family do,
/// and did the next digest actually use what they told us.
/// </summary>
/// <remarks>
/// Lives in Application so the API write path, the digest job, and the expiry worker can
/// increment the same instruments without reaching into Infrastructure. OpenTelemetry
/// subscribes by name (<see cref="TelemetryNames.QuestionnaireSource"/>), not by instance.
/// <para>
/// Privacy: tags are closed vocabularies (scope, origin). No question text, no answer text,
/// no member id.
/// </para>
/// </remarks>
public static class QuestionnaireTelemetry
{
    public static readonly Meter Meter = new(TelemetryNames.QuestionnaireSource);

    public static readonly Counter<long> Asked = Meter.CreateCounter<long>(
        "questionnaire.asked",
        description: "Questions the digest stored for a family, by scope");

    public static readonly Counter<long> Answered = Meter.CreateCounter<long>(
        "questionnaire.answered",
        description: "Questions a caregiver answered or edited, by scope and origin");

    public static readonly Counter<long> Dismissed = Meter.CreateCounter<long>(
        "questionnaire.dismissed",
        description: "Questions a caregiver skipped");

    public static readonly Counter<long> Expired = Meter.CreateCounter<long>(
        "questionnaire.expired",
        description: "Pending questions retired because the day they asked about had ended");

    public static readonly Counter<long> Offered = Meter.CreateCounter<long>(
        "questionnaire.offered",
        description: "Standing facts a caregiver volunteered, rather than answering a digest question");

    public static readonly Counter<long> DigestInformed = Meter.CreateCounter<long>(
        "questionnaire.digest.informed",
        description: "Family digests stored while standing or recent facts were in the prompt");

    public static readonly Counter<long> DigestRecited = Meter.CreateCounter<long>(
        "questionnaire.digest.recited",
        description: "Family digests discarded because they retold the family's answers");

    public const string ScopeTag = "questionnaire.scope";
    public const string OriginTag = "questionnaire.origin";

    public static void RecordAsked(QuestionnaireScope scope) =>
        Asked.Add(1, new KeyValuePair<string, object?>(ScopeTag, ScopeValue(scope)));

    public static void RecordAnswered(QuestionnaireScope scope, QuestionnaireOrigin origin) =>
        Answered.Add(1,
            new KeyValuePair<string, object?>(ScopeTag, ScopeValue(scope)),
            new KeyValuePair<string, object?>(OriginTag, OriginValue(origin)));

    public static void RecordDismissed() => Dismissed.Add(1);

    public static void RecordExpired(int count = 1)
    {
        if (count > 0)
            Expired.Add(count);
    }

    public static void RecordOffered() => Offered.Add(1);

    public static void RecordDigestInformed() => DigestInformed.Add(1);

    public static void RecordDigestRecited() => DigestRecited.Add(1);

    private static string ScopeValue(QuestionnaireScope scope) =>
        scope == QuestionnaireScope.Permanent ? "permanent" : "timescoped";

    private static string OriginValue(QuestionnaireOrigin origin) =>
        origin == QuestionnaireOrigin.Family ? "family" : "digest";
}
