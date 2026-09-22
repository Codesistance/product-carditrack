namespace CardiTrack.Application.Services;

/// <summary>
/// The canned answers a caregiver can give an alert — one short list for acknowledging it and one
/// for closing it, keyed by the producer's <c>rule</c> stamp.
/// </summary>
/// <remarks>
/// <para>
/// The point of a canned list is that answering costs one tap. A caregiver reading "Margaret
/// hasn't moved today" at 09:00 has somewhere to be; if saying "I called, she slept in" means
/// typing a sentence, most of the time nobody says anything and the rest of the family is left
/// guessing whether the alert was seen. So the codes are phrased as the thing the caregiver
/// actually did, not as statuses.
/// </para>
/// <para>
/// Acknowledge and close are different lists because they answer different questions.
/// Acknowledging says <em>somebody has this</em> and stops the escalation ladder; the alert is
/// still open. Closing says <em>it is dealt with</em>, and re-arms the rule — so its codes are
/// outcomes, not intentions.
/// </para>
/// <para>
/// Most rules share the generic lists. A rule gets its own only where a caregiver's real answer
/// is specific enough that the generic wording would be a worse fit than free text — a silent
/// watch is answered by charging it, which is not an outcome any health rule has. Rules without
/// an entry, and caregiver-defined alarms (<c>custom:{alarmId}</c>, whose wording nobody here can
/// anticipate), fall back to the generic lists rather than to an empty one: a screen offering no
/// chips is a screen that demands typing.
/// </para>
/// </remarks>
public static class AlertResponseCatalog
{
    /// <summary>Which of the two lists a code belongs to.</summary>
    public enum ResponseKind
    {
        Acknowledge,
        Close
    }

    private static readonly IReadOnlyList<AlertResponseOption> GenericAcknowledge =
    [
        new("calling", "Calling them now"),
        new("checking_in_person", "Going to check in person"),
        new("contacting_someone", "Asking someone nearby to check"),
        new("aware", "I know about this"),
    ];

    private static readonly IReadOnlyList<AlertResponseOption> GenericClose =
    [
        new("spoke_to_them", "Spoke to them — they're fine"),
        new("expected", "Expected, nothing wrong"),
        new("seen_by_clinician", "Seen by a doctor or nurse"),
        new("device_problem", "Not real — a watch problem"),
        new("handled_other", "Dealt with another way"),
    ];

    /// <summary>
    /// The rules whose answers are specific enough to be worth their own wording. Everything else
    /// uses the generic lists — deliberately, rather than inventing near-duplicates of them.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, RuleOptions> ByRule =
        new Dictionary<string, RuleOptions>(StringComparer.Ordinal)
        {
            [AlertRuleCatalogue.DeviceSilence] = new(
                Acknowledge:
                [
                    new("will_charge", "I'll get it charged"),
                    new("asking_them", "Asking them to put it back on"),
                    new("checking_in_person", "Going to check in person"),
                ],
                Close:
                [
                    // The only rule whose alert is about the equipment rather than the person, so
                    // every close here is about the watch and none of the health outcomes fit.
                    new("charged_and_worn", "Charged and back on"),
                    new("stopped_wearing", "They've stopped wearing it"),
                    new("away_from_phone", "Away from their phone — readings will catch up"),
                    new("handled_other", "Dealt with another way"),
                ]),

            [AlertRuleCatalogue.NoMorningActivity] = new(
                Acknowledge:
                [
                    new("calling", "Calling them now"),
                    new("checking_in_person", "Going to check in person"),
                    new("contacting_someone", "Asking someone nearby to check"),
                    new("aware", "I know about this"),
                ],
                Close:
                [
                    new("awake_and_fine", "Spoke to them — awake and fine"),
                    new("slept_in", "They slept in"),
                    new("away_from_home", "They're away from home"),
                    new("not_wearing_watch", "Watch wasn't on"),
                    new("seen_by_clinician", "Seen by a doctor or nurse"),
                    new("handled_other", "Dealt with another way"),
                ]),

            [AlertRuleCatalogue.IrregularSleep] = new(
                Acknowledge: GenericAcknowledge,
                Close:
                [
                    new("known_bad_night", "A known bad night"),
                    new("travel_or_visitors", "Travelling or had visitors"),
                    new("unwell", "They're unwell"),
                    new("seen_by_clinician", "Seen by a doctor or nurse"),
                    new("device_problem", "Not real — a watch problem"),
                    new("handled_other", "Dealt with another way"),
                ]),

            [AlertRuleCatalogue.ActivityDecline] = new(
                Acknowledge: GenericAcknowledge,
                Close:
                [
                    new("resting_day", "A deliberate quiet day"),
                    new("unwell", "They're unwell"),
                    new("not_wearing_watch", "Watch wasn't on"),
                    new("seen_by_clinician", "Seen by a doctor or nurse"),
                    new("handled_other", "Dealt with another way"),
                ]),
        };

    /// <summary>
    /// The options to offer for this rule and kind. Never empty: an unknown rule, a caregiver's
    /// own alarm, and a row raised before rule markers existed all get the generic list.
    /// </summary>
    public static IReadOnlyList<AlertResponseOption> For(string? rule, ResponseKind kind)
    {
        if (rule is not null && ByRule.TryGetValue(rule, out var options))
            return kind == ResponseKind.Acknowledge ? options.Acknowledge : options.Close;

        return kind == ResponseKind.Acknowledge ? GenericAcknowledge : GenericClose;
    }

    /// <summary>
    /// Whether <paramref name="code"/> is one this rule offers for this kind.
    /// </summary>
    /// <remarks>
    /// Checked server-side rather than trusted, for the ordinary reason: the client's list and
    /// this one are two copies of the same fact, and a stale app must be told its code no longer
    /// exists rather than storing one nothing can render a label for. A null code is valid — a
    /// note on its own is a complete answer.
    /// </remarks>
    public static bool IsValid(string? rule, ResponseKind kind, string? code) =>
        string.IsNullOrWhiteSpace(code)
        || For(rule, kind).Any(o => string.Equals(o.Code, code, StringComparison.Ordinal));

    /// <summary>The codes this rule offers for this kind, for an error message that names them.</summary>
    public static IReadOnlyList<string> CodesFor(string? rule, ResponseKind kind) =>
        For(rule, kind).Select(o => o.Code).ToList();

    private sealed record RuleOptions(
        IReadOnlyList<AlertResponseOption> Acknowledge,
        IReadOnlyList<AlertResponseOption> Close);
}

/// <summary>One canned answer: the stored code, and the words the caregiver taps.</summary>
/// <param name="Code">
/// Stable and stored on the response row. Never localised and never renamed — a stored code whose
/// meaning moved would re-label answers caregivers already gave.
/// </param>
/// <param name="Label">The wording shown on the chip. Free to change; nothing is keyed to it.</param>
public sealed record AlertResponseOption(string Code, string Label);
