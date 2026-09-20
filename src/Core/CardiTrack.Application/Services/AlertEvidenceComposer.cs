using System.Globalization;
using System.Text.Json;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Services;

/// <summary>
/// Why an alert fired, in the register a caregiver reads — the producer's own yardstick, composed
/// in .NET from the figures it stamped in <c>MetricValues</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every rule already states its yardstick (<see cref="StatisticalFinding.Observation"/> — "the
/// yardstick is two consecutive nights below their usual minus 12 ms"), but that sentence is prompt
/// input to MedGemma and never leaves the server. What reached the caregiver was the model's
/// narrative and a current-vs-usual pair, neither of which says what it <em>took</em> to fire. This
/// is that missing half, and it is deliberately authored code over stamped numbers rather than a
/// second model call: an explanation of a threshold that was itself generated is not evidence of
/// anything. It is also the concrete surface behind the transparency claim in
/// <c>docs/compliance/art22_alerting_analysis.md</c>.
/// </para>
/// <para>
/// It prefers the stamped margin (<c>marginBpm</c>, <c>marginMs</c>, <c>marginPerMinute</c>,
/// <c>thresholdMinutes</c>) over re-deriving one from the rule's constants, because the margin that
/// fired is the member's own — two standard deviations of <em>their</em> variability — and a
/// constant re-applied at read time would describe a different alert every time the baseline moved
/// underneath it. Where a producer stamped no margin, the sentence names the rule's constant and
/// stops short of a figure it cannot stand behind.
/// </para>
/// <para>
/// Nothing here names a condition, and nothing here is a second opinion on the reading: it says
/// what the rule watched and where its line sat, which is exactly the part a caregiver cannot
/// reconstruct and the part they need in order to disagree with it.
/// </para>
/// </remarks>
public static class AlertEvidenceComposer
{
    /// <summary>
    /// The evidence block, or null when the row cannot honestly produce one — a markerless legacy
    /// alert, or a rule this build does not know. Silence is the right answer there: an evidence
    /// card that shrugs is worse than no card, because the caregiver reads its presence as a claim.
    /// </summary>
    public static AlertEvidenceResponse? Compose(
        string? rule, JsonElement metrics, PatternBaseline? baseline)
    {
        if (string.IsNullOrWhiteSpace(rule))
            return null;

        var evidence = rule.StartsWith(AlertRuleCatalogue.CustomRulePrefix, StringComparison.Ordinal)
            ? CustomAlarm(metrics)
            : BuiltIn(rule, metrics, baseline);

        if (evidence is not { } found)
            return null;

        return new AlertEvidenceResponse
        {
            RuleLabel = found.Label,
            WhyLine = found.Why,
            ThresholdLabel = found.Threshold,
            // The window the "usual" in that sentence was learned over — and only where there is
            // such a usual in it. Two ways this stays null. A rule that never reads a baseline
            // (the trend, both pairing rules, device silence, a caregiver's own alarm) has no
            // "usual" in its line at all, and the card would otherwise append "measured against
            // their last 30 days" to a sentence that is purely week-over-week or a fixed
            // threshold. And a rule that would have read one but got nothing back took its
            // figures from the stamp alone, where naming a window we did not read is the same
            // invention. Both are the thing this class exists to avoid.
            BaselinePeriodDays = found.AgainstUsual ? baseline?.PeriodDays : null,
        };
    }

    /// <summary>
    /// The fraction of their usual a reading has to fall to before the medium-sensitivity rules
    /// count it as a departure — the one place this is turned into a figure rather than a
    /// percentage, so the chip and <see cref="StatisticalAlertRules.DeviationFraction"/> cannot
    /// drift apart.
    /// </summary>
    private const decimal BelowUsual = 1m - (decimal)StatisticalAlertRules.DeviationFraction;

    /// <summary>One rule's evidence: the settings-screen name, the sentence, and the line it crossed.</summary>
    /// <param name="AgainstUsual">
    /// Whether the sentence measures against the member's own learned usual. Carried per rule
    /// rather than inferred from the presence of a baseline, because the two differ: a baseline is
    /// read for the member, and whether a given rule's line quotes one is a property of the rule.
    /// </param>
    private readonly record struct Evidence(
        string Label, string Why, string? Threshold, bool AgainstUsual);

    private static Evidence? BuiltIn(string rule, JsonElement metrics, PatternBaseline? baseline)
    {
        // The name this rule carries on the alert-settings screen, so the card a caregiver is
        // reading and the toggle that would turn it off are visibly the same thing.
        if (AlertRuleCatalogue.Find(rule) is not { } definition)
            return null;

        var (why, threshold, againstUsual) = rule switch
        {
            StatisticalAlertRules.ActivityDeclineRule => Steps(metrics, baseline),
            StatisticalAlertRules.IrregularSleepRule => Sleep(metrics, baseline),
            StatisticalAlertRules.ElevatedHeartRateRule => HeartRate(metrics, baseline),
            StatisticalAlertRules.NoMorningActivityRule => NoMorning(metrics, baseline),
            StatisticalAlertRules.LongTermTrendRule => Trend(),
            StatisticalAlertRules.HeartRateVariabilityDropRule => HeartRateVariability(metrics, baseline),
            StatisticalAlertRules.OvernightBreathingUpRule => OvernightBreathing(metrics, baseline),
            StatisticalAlertRules.ElevatedZoneWithoutMovementRule => ElevatedZone(metrics),
            StatisticalAlertRules.DaytimeInactivityBlockRule => SedentaryStretch(metrics),
            AlertDetailComposer.RealtimeHeartRateRule => RealtimeHeartRate(),
            AlertDetailComposer.DeviceSilenceRule => DeviceSilence(metrics),
            // A catalogue entry whose producer has not shipped cannot have raised this alert. If
            // one ever does, it arrives here unexplained rather than explained wrongly.
            _ => (null, null, false),
        };

        return why is null ? null : new Evidence(definition.Title, why, threshold, againstUsual);
    }

    private static (string? Why, string? Threshold, bool AgainstUsual) Steps(JsonElement metrics, PatternBaseline? baseline)
    {
        var usual = Read(metrics, "baselineAvgSteps") ?? baseline?.AvgSteps;
        var why = $"This is raised when a day's steps land more than {StatisticalAlertRules.DeviationFraction:P0} "
            + "below their own usual, measured against the days before it rather than against anyone else's day.";

        return (why, usual is > 0 ? $"Below {usual.Value * BelowUsual:N0} steps" : null, true);
    }

    private static (string? Why, string? Threshold, bool AgainstUsual) Sleep(JsonElement metrics, PatternBaseline? baseline)
    {
        var usual = Read(metrics, "baselineAvgSleepMinutes") ?? baseline?.AvgSleepMinutes;
        var why = $"This is raised when a night sits more than {StatisticalAlertRules.DeviationFraction:P0} "
            + "away from their usual. A shorter night always counts; a longer one counts only when it "
            + "also runs past the hours recommended at their age, because more sleep than usual is "
            + "not on its own something to be told about.";

        // Which side the night fell on decides which line to name. The rule is symmetric on the
        // way in but not on the way out — a longer night only counts once it passes the ceiling
        // recommended at their age — so a long-sleep alert quoting "shorter than" would print the
        // opposite of the thing that raised it.
        var slept = Read(metrics, "sleepMinutes");
        var longer = slept is { } minutes && usual is { } average && minutes > average;

        if (longer)
        {
            return (why, Read(metrics, "recommendedHighHours") is { } ceiling
                ? $"Longer than {ceiling:0.#} h, their recommended ceiling"
                : null, true);
        }

        return (why, usual is > 0 ? $"Shorter than {Hours(usual.Value * BelowUsual)} h" : null, true);
    }

    private static (string? Why, string? Threshold, bool AgainstUsual) HeartRate(JsonElement metrics, PatternBaseline? baseline)
    {
        var usual = Read(metrics, "baselineAvgRestingHeartRate") ?? baseline?.AvgRestingHeartRate;
        var margin = Read(metrics, "marginBpm");
        var why = margin is { } m
            ? $"This is raised when a resting heart rate sits more than {m:0.#} bpm above their own "
              + $"usual — the larger of two standard deviations of their own variation and "
              + $"{StatisticalAlertRules.HrMarginFloorBpm} bpm, so a steady heart is not flagged for ordinary movement."
            : "This is raised when a resting heart rate sits above their own usual by the larger of "
              + $"two standard deviations of their own variation and {StatisticalAlertRules.HrMarginFloorBpm} bpm.";

        return (why, usual is > 0 && margin is { } bpm ? $"Above {usual.Value + bpm:0} bpm" : null, true);
    }

    private static (string? Why, string? Threshold, bool AgainstUsual) NoMorning(JsonElement metrics, PatternBaseline? baseline)
    {
        var wake = ReadText(metrics, "typicalWakeTime")
            ?? baseline?.TypicalWakeTime?.ToString("HH:mm", CultureInfo.InvariantCulture);
        var why = "This is raised only when the watch is reporting and has measured a zero — no steps "
            + $"at all — more than {StatisticalAlertRules.MorningGraceHours} hours past their usual waking "
            + "time. A watch that has simply stopped sending is a different alert, because not measured "
            + "and did not move are not the same thing.";

        return (why, DeadlineFrom(wake) is { } deadline ? $"No steps by {deadline}" : null, true);
    }

    private static (string? Why, string? Threshold, bool AgainstUsual) Trend() =>
        ($"This is raised when each of {StatisticalAlertRules.TrendWeeks} weeks running averaged at least "
         + $"{StatisticalAlertRules.WeeklyDeclineFraction:P0} fewer steps a day than the week before it. "
         + "One quiet week never raises it — the finding is the direction, held for a month.",
         $"{StatisticalAlertRules.TrendWeeks} weeks, each at least {StatisticalAlertRules.WeeklyDeclineFraction:P0} below the last",
         // Each week is measured against the week before it, never against a learned baseline —
         // this composer takes none and reads none. The card appending "measured against their
         // last 30 days" here named a window nothing in the line came from.
         false);

    private static (string? Why, string? Threshold, bool AgainstUsual) HeartRateVariability(JsonElement metrics, PatternBaseline? baseline)
    {
        var usual = Read(metrics, "baselineAvgHeartRateVariabilityMs") ?? baseline?.AvgHeartRateVariabilityMs;
        var margin = Read(metrics, "marginMs");
        var why = margin is { } m
            ? $"This is raised when overnight heart rate variability sits more than {m:0.#} ms below their "
              + "own usual on both of the last two nights, not one. A single night is moved by a late meal "
              + "or a poor night's sleep in someone with nothing wrong at all, so one night never raises it."
            : "This is raised when overnight heart rate variability sits below their own usual on both of "
              + "the last two nights, not one. A single night never raises it.";

        return (why, usual is > 0 && margin is { } ms
            ? $"Below {usual.Value - ms:0.#} ms, two nights running"
            : null, true);
    }

    private static (string? Why, string? Threshold, bool AgainstUsual) OvernightBreathing(JsonElement metrics, PatternBaseline? baseline)
    {
        var usual = Read(metrics, "baselineAvgOvernightBreathingRate") ?? baseline?.AvgOvernightBreathingRate;
        var margin = Read(metrics, "marginPerMinute");
        var why = margin is { } m
            ? $"This is raised when breathing while asleep runs more than {m:0.#} a minute above their own "
              + "usual. It is measured asleep rather than across the day because a whole-day average moves "
              + "with a stair climb and a nap, and would hide a change this size."
            : "This is raised when breathing while asleep runs above their own usual by the larger of two "
              + $"standard deviations and {StatisticalAlertRules.BreathingMarginFloorPerMinute:0.#} a minute.";

        return (why, usual is > 0 && margin is { } rate ? $"Above {usual.Value + rate:0.#} a minute" : null, true);
    }

    private static (string? Why, string? Threshold, bool AgainstUsual) ElevatedZone(JsonElement metrics)
    {
        var threshold = Read(metrics, "thresholdMinutes");
        var why = "This is raised only when both halves are true at once: a day whose steps already count "
            + "as a decline, and real time spent with the heart in a raised zone on that same day. The "
            + "pairing is the finding — raised minutes after a walk are exercise, and the same minutes on "
            + "a day they barely moved are not.";

        // False despite "already count as a decline": the producer judged that half against a
        // baseline, but this composer reads none, and the figure the line actually quotes is a
        // minutes threshold. Naming a window we did not look at is the invention to avoid, and
        // attaching it to the raised-minutes half would put it on the wrong one besides.
        return (why, threshold is { } minutes
            ? $"More than {minutes:0} raised minutes on a quiet day"
            : $"More than {StatisticalAlertRules.ElevatedZoneFloorMinutes} raised minutes on a quiet day",
            false);
    }

    private static (string? Why, string? Threshold, bool AgainstUsual) SedentaryStretch(JsonElement metrics)
    {
        var threshold = Read(metrics, "thresholdMinutes");
        var why = "This is raised when one unbroken still stretch in waking hours runs past the longer of "
            + $"{StatisticalAlertRules.SedentaryStretchFloorMinutes / 60} hours and half again their own "
            + "usual longest. Both halves matter: the floor keeps someone who normally breaks every forty "
            + "minutes from being flagged at an hour, and the margin keeps someone who habitually sits for "
            + "three hours from being flagged every afternoon.";

        // Same as the pairing rule above: "half again their own usual longest" is a personal
        // figure the producer worked out and stamped, not one of this baseline's columns, so the
        // baseline's window does not describe where it came from.
        return (why, threshold is { } minutes ? $"Longer than {Hours(minutes)} h" : null, false);
    }

    private static (string? Why, string? Threshold, bool AgainstUsual) RealtimeHeartRate() =>
        ("This is raised when the last hour of heart-rate readings changes shape — a jump at least "
         + $"{DigestRefreshRules.SampleJumpScore:0} times their own typical minute-to-minute jitter away from "
         + "that hour's trend. It is a change in pattern, not a reading crossing a fixed rate.",
         $"A deviation of {DigestRefreshRules.SampleJumpScore:0} typical jitters or more from the hour's trend",
         // Their own typical jitter, measured within the hour it is judging — not a baseline.
         false);

    private static (string? Why, string? Threshold, bool AgainstUsual) DeviceSilence(JsonElement metrics)
    {
        var threshold = Read(metrics, "thresholdMinutes");
        var why = "This is about the watch, not about them: no readings have arrived for a stretch of their "
            + "waking hours. It usually means the watch needs charging or is not being worn, and it is "
            + "deliberately never read as stillness — nothing was measured, so nothing can be concluded.";

        // About the watch, not about them: nothing here is measured against a usual at all.
        return (why, threshold is { } minutes ? $"No readings for {Hours(minutes)} h of waking time" : null, false);
    }

    /// <summary>
    /// A caregiver-defined alarm explains itself: the condition sentence the builder previewed when
    /// they set it up is stamped on the alert, so the evidence here is their own instruction read
    /// back to them rather than anything this system decided.
    /// </summary>
    private static Evidence? CustomAlarm(JsonElement metrics)
    {
        var condition = ReadText(metrics, "condition");
        if (condition is null)
            return null;

        var label = ReadText(metrics, "alarmName") ?? "Your alarm";
        var why = $"You asked to be told when {Lowered(condition)} This is your own alarm, not one of "
            + "CardiTrack's rules — the level, the window and how urgent it is were all set by you, and "
            + "changing them changes when this arrives.";

        // Their own level, their own window. Nothing about a learned usual enters this.
        return new Evidence(label, why, ThresholdOf(metrics), AgainstUsual: false);
    }

    /// <summary>
    /// The level the alarm actually fired on. <c>effectiveThreshold</c> rather than the configured
    /// one wherever they differ: a baseline-relative alarm is configured as a percentage and fires
    /// on the figure that percentage resolved to on the day, and a caregiver checking the alert
    /// against the reading needs the second number.
    /// </summary>
    private static string? ThresholdOf(JsonElement metrics)
    {
        var threshold = Read(metrics, "effectiveThreshold") ?? Read(metrics, "configuredThreshold");
        if (threshold is not { } level)
            return null;

        // Same catalogue lookup the comparison card's two columns use, so the chip and the columns
        // cannot end up quoting one level in two units.
        var unit = AlertDetailComposer.AlarmUnit(metrics);

        return unit is null ? $"{level:0.#}" : $"{level:0.#} {unit}";
    }

    /// <summary>
    /// The condition sentence starts a clause here rather than a sentence, so its first word is
    /// lowered — unless it opens on something that is capitalised in its own right (a metric name
    /// such as "SpO2"), where lowering it would misspell the thing being measured.
    /// </summary>
    private static string Lowered(string condition)
    {
        var trimmed = condition.Trim();
        if (trimmed.Length == 0)
            return trimmed;

        var firstWord = trimmed.Split(' ', 2)[0];
        var isAcronym = firstWord.Count(char.IsUpper) > 1;

        return isAcronym
            ? trimmed
            : char.ToLowerInvariant(trimmed[0]) + trimmed[1..];
    }

    /// <summary>The wake time plus the rule's grace, on the same wall clock the stamp used.</summary>
    private static string? DeadlineFrom(string? wake) =>
        TimeOnly.TryParse(wake, CultureInfo.InvariantCulture, out var time)
            ? time.AddHours(StatisticalAlertRules.MorningGraceHours)
                .ToString("HH:mm", CultureInfo.InvariantCulture)
            : null;

    private static string Hours(decimal minutes) =>
        (minutes / 60m).ToString("0.#", CultureInfo.InvariantCulture);

    private static decimal? Read(JsonElement metrics, string name) =>
        AlertDetailComposer.ReadDecimal(metrics, name);

    private static string? ReadText(JsonElement metrics, string name) =>
        AlertDetailComposer.ReadString(metrics, name);
}
