using System.Globalization;
using System.Text.Json;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <summary>One rule's verdict: everything the orchestrator needs to write the alert.
/// <see cref="NightOf"/> is set by rules that judge one specific night (the civil day the
/// night ended on) rather than the firing day's data — the orchestrator dedups those per
/// night, because late-arriving data can put the same night in front of the rule on two
/// calendar days.</summary>
public sealed record StatisticalAlertCandidate(
    string Rule, AlertType Type, AlertSeverity Severity, string Title, string Message, string MetricValues,
    DateOnly? NightOf = null);

/// <summary>
/// The R1 statistical alert rules (docs/execution/backend/api/alerts.md taxonomy) — pure
/// functions from baseline + daily readings to an alert candidate or null, deliberately free of
/// I/O so every threshold is unit-testable to its boundary. Thresholds are the hard-coded
/// "medium" sensitivity profile (deviation &gt; 30%); the low/high profiles wait on wiring
/// <c>CardiMember.AlertSensitivity</c>. Per-rule on/off lives in <c>AlertPreference</c>.
/// <para>
/// Every rule takes the <b>established 30-day</b> baseline only — provisional 7/14-day
/// baselines never alert (a statistically thin window would trade the &lt;5% false-positive
/// target for early noise), which the orchestrator enforces by what it fetches.
/// </para>
/// <para>
/// Null readings never fire anything: null means "not measured", and the null-vs-zero
/// discipline holds here exactly as it does in ingestion — a day the device did not measure is
/// not a day the member did nothing.
/// </para>
/// </summary>
public static class StatisticalAlertRules
{
    public const string ActivityDeclineRule = "activity_decline";
    public const string IrregularSleepRule = "irregular_sleep";
    public const string ElevatedHeartRateRule = "elevated_heart_rate";
    public const string NoMorningActivityRule = "no_morning_activity";
    public const string LongTermTrendRule = "long_term_trend";
    public const string HeartRateVariabilityDropRule = "hrv_drop";
    public const string OvernightBreathingUpRule = "overnight_breathing_up";
    public const string ElevatedZoneWithoutMovementRule = "elevated_zone_without_movement";
    public const string DaytimeInactivityBlockRule = "daytime_inactivity_block";

    /// <summary>Medium sensitivity: a reading more than 30% off its baseline is worth a word.</summary>
    public const double DeviationFraction = 0.30;

    /// <summary>
    /// Elevated resting HR margin: 2σ of the member's own variability, floored at 5 bpm so a
    /// member with an unusually steady heart does not get alerted over ordinary variation.
    /// </summary>
    public const int HrSigmaMultiplier = 2;
    public const int HrMarginFloorBpm = 5;

    /// <summary>Grace after the typical wake time before "no movement yet" means anything —
    /// nobody owes their wearable a step count before the kettle has boiled.</summary>
    public const int MorningGraceHours = 2;

    /// <summary>Long-term trend: ≥5% decline week-over-week, sustained across 4 weeks.</summary>
    public const double WeeklyDeclineFraction = 0.05;
    public const int TrendWeeks = 4;

    /// <summary>Days with a steps reading a week needs before its average means anything.</summary>
    public const int TrendMinDaysPerWeek = 4;

    /// <summary>
    /// HRV drop margin: 2σ of the member's own night-to-night variability, floored at 15% of their
    /// baseline. The floor is proportional where the heart-rate one is absolute (5 bpm) because
    /// overnight RMSSD is not comparable between people — a healthy 80-year-old may sit near 15 ms
    /// where a healthy 40-year-old sits near 60, and a fixed millisecond floor would be untrippable
    /// for the first and permanently tripped for the second.
    /// </summary>
    public const int HrvSigmaMultiplier = 2;
    public const decimal HrvMarginFloorFraction = 0.15m;

    /// <summary>
    /// Overnight breathing margin: 2σ of the member's own night-to-night variability, floored at
    /// 1 breath per minute. The floor is absolute where HRV's is proportional, because respiratory
    /// rate does not span between people the way RMSSD does — every adult sits in the low-to-mid
    /// teens asleep, and a rise of a breath a minute means the same thing at 13 as at 17.
    /// </summary>
    public const int BreathingSigmaMultiplier = 2;
    public const decimal BreathingMarginFloorPerMinute = 1m;

    /// <summary>
    /// Elevated-zone minutes that count as the heart having worked, on a day the steps say the
    /// member did not. Floored rather than purely baseline-relative because a member whose usual
    /// is near zero would otherwise be alerted by ten minutes of gardening.
    /// </summary>
    public const int ElevatedZoneFloorMinutes = 25;

    /// <summary>
    /// The longest unbroken sedentary stretch that is worth a word, and the margin over the
    /// member's own usual. Three hours is the floor because a nap, a long film and an afternoon in
    /// a chair are all ordinary; what is not ordinary is one of those becoming four hours where
    /// this member's own day usually breaks after two.
    /// </summary>
    public const int SedentaryStretchFloorMinutes = 180;
    public const double SedentaryStretchMarginFraction = 0.5;

    /// <summary>Yesterday's steps more than 30% below the baseline average.</summary>
    public static StatisticalAlertCandidate? ActivityDecline(PatternBaseline baseline, ActivityLog? yesterday)
    {
        if (baseline.AvgSteps is not > 0 || yesterday?.Steps is not { } steps)
            return null;

        var average = baseline.AvgSteps.Value;
        if (steps >= average * (1 - DeviationFraction))
            return null;

        return new StatisticalAlertCandidate(
            ActivityDeclineRule, AlertType.Inactivity, AlertSeverity.Yellow,
            "Activity was well below the usual",
            $"About {steps:N0} steps yesterday against a usual {average:N0} — a quieter day than normal. "
            + "Worth a gentle check-in.",
            Serialize(new
            {
                rule = ActivityDeclineRule,
                day = yesterday.Date.ToString("O"),
                steps,
                baselineAvgSteps = average,
            }));
    }

    /// <summary>
    /// Whether the night departs from the baseline average far enough to be worth a word — the
    /// trigger on its own, without the grading <see cref="IrregularSleep"/> puts on it. Split out
    /// for the digest, which asks only whether today's readings would fire a rule and has no
    /// business knowing the member's age to find out.
    /// </summary>
    public static bool SleepDepartsFromBaseline(PatternBaseline baseline, ActivityLog? lastNight)
    {
        if (baseline.AvgSleepMinutes is not > 0 || lastNight?.SleepMinutes is not { } sleep)
            return false;

        var average = baseline.AvgSleepMinutes.Value;
        return Math.Abs(sleep - average) > average * DeviationFraction;
    }

    /// <summary>
    /// The most recent night's sleep more than 30% off the baseline average, in either direction.
    /// Sleep sessions are attributed to the civil day they <b>ended</b> on, so last night lives on
    /// <em>today's</em> log — the same row the dashboard's sleep card rates — and the orchestrator
    /// passes the freshest log that carries a sleep reading. The candidate names the night it
    /// judged (<see cref="StatisticalAlertCandidate.NightOf"/>) so one night alerts at most once
    /// however late its data arrived.
    /// <para>
    /// The trigger is symmetric; <b>what it alerts on is not</b>. A departure from the member's
    /// own usual cannot say on its own whether the night was a problem, because the usual it is
    /// measured against may itself be far short of what anyone should be getting: a member who
    /// normally manages 3.8 hours and slept 5.2 is 37% off their baseline and closer to the
    /// published recommendation than they have been all fortnight. That is an improvement, and it
    /// is <em>retrospective</em> — the night is over by the time anyone reads about it, and there
    /// is nothing a caregiver can do in the morning about sleep that has already happened. So a
    /// longer night that has not overshot the recommended band now raises <b>no alert at all</b>:
    /// the fact belongs in the daybook entry, which describes the finished day, rather than on a
    /// screen whose job is to say what needs attention now. It stays an alert in the one
    /// direction where more sleep is worth flagging — past the recommended ceiling at their age.
    /// A shorter night keeps its
    /// <see cref="AlertSeverity.Yellow"/> whatever the absolute figure, because a sudden loss of a
    /// third of someone's sleep is a pattern break in its own right.
    /// </para>
    /// </summary>
    /// <param name="ageYears">
    /// The member's age, for the published band the night is graded against — see
    /// <see cref="HealthReferenceRanges.Sleep"/>. Only the ceiling moves with it, and the ceiling
    /// is exactly what decides whether a longer night is a concern, so a default here would quietly
    /// grant every older adult an hour of oversleep the recommendation does not give them.
    /// </param>
    public static StatisticalAlertCandidate? IrregularSleep(
        PatternBaseline baseline, ActivityLog? lastNight, int ageYears)
    {
        if (lastNight?.SleepMinutes is not { } sleep
            || baseline.AvgSleepMinutes is not { } average
            || !SleepDepartsFromBaseline(baseline, lastNight))
        {
            return null;
        }

        var recommended = HealthReferenceRanges.Sleep(ageYears);
        // Exact, never rounded: this is what the band comparisons below threshold on, and
        // MemberInsightsCalculator documents the trap — 418 minutes is 6.97 hours and rounds to
        // 7.0, clearing a floor it is three minutes short of. Rounding happens at format time only.
        var hours = sleep / 60m;
        var usualHours = average / 60m;
        var longer = sleep > average;
        var overshot = hours > recommended.High;

        // A longer night that has not overshot is the one departure this rule can positively
        // establish was benign — and a benign departure from a night that is already over is not
        // an alert, it is a line in the daybook entry. Returning null here rather than grading it
        // Green is what stops a caregiver being paged about an improvement they cannot act on.
        if (longer && !overshot)
            return null;

        // The clause that says where the night landed against the recommendation, which is the
        // fact the deviation from their own usual leaves the caregiver to infer. Past the guard
        // above, a longer night is necessarily one that overshot the ceiling, so the two benign
        // sub-cases this expression used to carry went with the alert they described.
        var tail = longer
            ? $"and past the {recommended.High:0.#} hours recommended at their age. One night is "
              + "rarely a worry, but it may be worth mentioning."
            : "one night is rarely a worry, but it may be worth mentioning.";

        return new StatisticalAlertCandidate(
            IrregularSleepRule, AlertType.Sleep,
            AlertSeverity.Yellow,
            "Sleep was well off the usual",
            $"Around {hours:0.#} hours of sleep, noticeably {(longer ? "more" : "less")} "
            + $"than the usual {usualHours:0.#} — {tail}",
            Serialize(new
            {
                rule = IrregularSleepRule,
                night = lastNight.Date.ToString("O"),
                sleepMinutes = sleep,
                baselineAvgSleepMinutes = average,
                // The band this night was judged against, stored rather than re-derived later: a
                // member who crosses OlderAdultAge after the fact must not have the alert's copy
                // quoting one ceiling while the chart beside it draws another.
                recommendedLowHours = recommended.Low,
                recommendedHighHours = recommended.High,
            }),
            NightOf: lastNight.Date);
    }

    /// <summary>Yesterday's resting heart rate above baseline average + max(2σ, 5 bpm).</summary>
    public static StatisticalAlertCandidate? ElevatedHeartRate(PatternBaseline baseline, ActivityLog? yesterday)
    {
        if (baseline.AvgRestingHeartRate is not > 0 || yesterday?.RestingHeartRate is not { } restingHr)
            return null;

        var average = baseline.AvgRestingHeartRate.Value;
        var margin = Math.Max(
            HrSigmaMultiplier * (double)(baseline.StdDevHeartRate ?? 0), HrMarginFloorBpm);
        if (restingHr <= average + margin)
            return null;

        return new StatisticalAlertCandidate(
            ElevatedHeartRateRule, AlertType.HeartRate, AlertSeverity.Orange,
            "Resting heart rate is running high",
            $"Resting heart rate was {restingHr} bpm yesterday, clearly above the usual "
            + $"{average} bpm. Worth checking in today.",
            Serialize(new
            {
                rule = ElevatedHeartRateRule,
                day = yesterday.Date.ToString("O"),
                restingHeartRate = restingHr,
                baselineAvgRestingHeartRate = average,
                marginBpm = Math.Round(margin, 1),
            }));
    }

    /// <summary>
    /// The device is syncing today — today's log exists and carries a <b>measured zero</b>
    /// steps — yet the member's typical wake time passed more than the grace period ago.
    /// A null steps value never fires: not measured is not the same as not moving.
    /// </summary>
    public static StatisticalAlertCandidate? NoMorningActivity(
        PatternBaseline baseline, ActivityLog? today, DateTime localNow)
    {
        if (baseline.TypicalWakeTime is not { } wake || today?.Steps is not 0)
            return null;

        var earliest = wake.ToTimeSpan().Add(TimeSpan.FromHours(MorningGraceHours));
        if (localNow.TimeOfDay < earliest)
            return null;

        return new StatisticalAlertCandidate(
            NoMorningActivityRule, AlertType.PatternBreak, AlertSeverity.Red,
            "No movement since waking time",
            $"The device is reporting, but no steps have been recorded today — well past the "
            + $"usual waking time of {wake:HH\\:mm}. Please check in.",
            Serialize(new { rule = NoMorningActivityRule, typicalWakeTime = wake.ToString("HH:mm") }));
    }

    /// <summary>
    /// Weekly step averages declining ≥5% week-over-week for 4 consecutive weeks (ending
    /// yesterday). Each week needs enough measured days for its average to mean anything.
    /// </summary>
    public static StatisticalAlertCandidate? LongTermTrend(
        IReadOnlyDictionary<DateOnly, ActivityLog> logsByDate, DateOnly yesterday)
    {
        var weeklyAverages = new double[TrendWeeks];
        for (var week = 0; week < TrendWeeks; week++)
        {
            var weekEnd = yesterday.AddDays(-7 * week);
            var days = Enumerable.Range(0, 7)
                .Select(offset => logsByDate.GetValueOrDefault(weekEnd.AddDays(-offset))?.Steps)
                .OfType<int>()
                .ToList();
            if (days.Count < TrendMinDaysPerWeek)
                return null;

            weeklyAverages[week] = days.Average();
        }

        // Index 0 is the newest week; every week must sit ≥5% below the one before it.
        for (var week = 0; week < TrendWeeks - 1; week++)
        {
            var older = weeklyAverages[week + 1];
            if (older <= 0 || weeklyAverages[week] > older * (1 - WeeklyDeclineFraction))
                return null;
        }

        var totalDecline = 1 - weeklyAverages[0] / weeklyAverages[^1];
        return new StatisticalAlertCandidate(
            LongTermTrendRule, AlertType.Trend, AlertSeverity.Orange,
            "Activity has been declining for weeks",
            $"Daily steps have fallen steadily for {TrendWeeks} weeks — about "
            + $"{totalDecline:P0} lower than a month ago. A pattern like this is worth a "
            + "conversation, and perhaps a mention to a doctor.",
            Serialize(new
            {
                rule = LongTermTrendRule,
                day = yesterday.ToString("O"),
                weeklyAvgSteps = weeklyAverages.Reverse().Select(a => Math.Round(a)).ToArray(),
                declineFraction = Math.Round(totalDecline, 2),
            }));
    }

    /// <summary>
    /// Overnight heart rate variability below baseline − max(2σ, 15%) on <b>both</b> of the last
    /// two nights.
    /// </summary>
    /// <remarks>
    /// A fall in HRV is the earliest of the signals this engine watches — it moves before resting
    /// heart rate does when someone is coming down with something or their heart is under strain —
    /// and it is also the noisiest. A single night is moved by a late meal, a glass of wine or a
    /// bad night's sleep in a person with nothing wrong at all, so one night never fires this rule:
    /// both of the last two must sit below the threshold. A missing previous night is not
    /// permission to fire on one night either — it is one night, and the rule stays silent.
    /// <para>
    /// Filed as <see cref="AlertType.HeartRate"/> deliberately, which shares one cooldown across
    /// every producer of that type: a member whose HRV has dropped is often the same member whose
    /// resting rate has risen, and the family needs one "check on them", not two.
    /// </para>
    /// </remarks>
    public static StatisticalAlertCandidate? HeartRateVariabilityDrop(
        PatternBaseline baseline, ActivityLog? lastNight, ActivityLog? previousNight)
    {
        if (baseline.AvgHeartRateVariabilityMs is not > 0
            || lastNight?.HeartRateVariabilityMs is not { } latest
            || previousNight?.HeartRateVariabilityMs is not { } previous)
        {
            return null;
        }

        var average = baseline.AvgHeartRateVariabilityMs.Value;
        var margin = Math.Max(
            HrvSigmaMultiplier * (baseline.StdDevHeartRateVariability ?? 0m),
            average * HrvMarginFloorFraction);
        var threshold = average - margin;

        if (latest >= threshold || previous >= threshold)
            return null;

        return new StatisticalAlertCandidate(
            HeartRateVariabilityDropRule, AlertType.HeartRate, AlertSeverity.Orange,
            "Their heart rate variability has dropped",
            $"Overnight heart rate variability has been low two nights running — {latest:0.#} ms "
            + $"last night against a usual {average:0.#} ms. On its own it often just means a "
            + "poor night or a cold coming on, but it is worth a check-in.",
            Serialize(new
            {
                rule = HeartRateVariabilityDropRule,
                day = lastNight.Date.ToString("O"),
                heartRateVariabilityMs = latest,
                previousNightHeartRateVariabilityMs = previous,
                baselineAvgHeartRateVariabilityMs = average,
                marginMs = Math.Round(margin, 1),
            }),
            NightOf: lastNight.Date);
    }

    /// <summary>
    /// Last night's breathing rate above the member's own usual by max(2σ, 1 breath/min).
    /// </summary>
    /// <remarks>
    /// The overnight figure, not the daily one: a whole-day average mixes a stair climb with a nap
    /// and moves for reasons that have nothing to do with health, while a night is hours of
    /// stillness measured the same way every time. That is what makes a rise of one or two breaths
    /// a minute mean something — it is the earliest cheap signal there is of a chest infection or
    /// of fluid gathering, and it usually moves before the resting heart rate does.
    /// <para>
    /// Compared against the member and not against the published band. WHO's adult range (12-20)
    /// is wide enough that someone can climb four breaths a minute inside it, which is a real
    /// change hidden by a normal-looking number; the band is quoted in the copy for context, but
    /// what fires the rule is their own night-to-night usual.
    /// </para>
    /// </remarks>
    public static StatisticalAlertCandidate? OvernightBreathingUp(
        PatternBaseline baseline, ActivityLog? lastNight)
    {
        if (baseline.AvgOvernightBreathingRate is not > 0
            || lastNight?.OvernightBreathingRate is not { } breathing)
        {
            return null;
        }

        var average = baseline.AvgOvernightBreathingRate.Value;
        var margin = Math.Max(
            BreathingSigmaMultiplier * (baseline.StdDevOvernightBreathingRate ?? 0m),
            BreathingMarginFloorPerMinute);
        if (breathing <= average + margin)
            return null;

        var band = HealthReferenceRanges.BreathingRate;
        return new StatisticalAlertCandidate(
            OvernightBreathingUpRule, AlertType.PatternBreak, AlertSeverity.Orange,
            "They were breathing faster than usual overnight",
            $"Breathing averaged {breathing:0.#} a minute while they slept, against a usual "
            + $"{average:0.#}. A rise like this is often the first sign of a cold or chest "
            + "infection coming on — worth a check-in, and worth watching over the next night or two.",
            Serialize(new
            {
                rule = OvernightBreathingUpRule,
                day = lastNight.Date.ToString("O"),
                overnightBreathingRate = breathing,
                baselineAvgOvernightBreathingRate = average,
                marginPerMinute = Math.Round(margin, 1),
                recommendedLowPerMinute = band.Low,
                recommendedHighPerMinute = band.High,
            }),
            NightOf: lastNight.Date);
    }

    /// <summary>
    /// Yesterday's heart spent real time above the light zone on a day the member barely moved.
    /// </summary>
    /// <remarks>
    /// The pairing is the finding, not either half. Elevated zone minutes after a walk are what
    /// exercise looks like; the same minutes on a day of 1,200 steps are a heart working without
    /// being asked to, which is what a fever, an arrhythmia, pain or dehydration look like from the
    /// outside. It is also the one signal here that a step count alone actively hides: a still day
    /// reads as restful, and this is the case where it is not.
    /// <para>
    /// Both halves are measured against this member. The steps side reuses
    /// <see cref="ActivityDecline"/> so the two rules cannot disagree about what a quiet day is,
    /// and the zone side takes the larger of their own usual and a floor, so a member who normally
    /// records no elevated minutes at all is not alerted by ten minutes of gardening.
    /// </para>
    /// </remarks>
    public static StatisticalAlertCandidate? ElevatedZoneWithoutMovement(
        PatternBaseline baseline, ActivityLog? yesterday)
    {
        if (yesterday is null || ActivityDecline(baseline, yesterday) is null)
            return null;

        if (BaselineCalculator.ElevatedZoneMinutes(yesterday) is not { } elevated)
            return null;

        var threshold = Math.Max(baseline.AvgElevatedZoneMinutes ?? 0, ElevatedZoneFloorMinutes);
        if (elevated <= threshold)
            return null;

        var zoneFloor = yesterday.ModerateZoneFloorBpm is { } floor
            ? $" — above {floor} bpm, where their watch puts the start of real effort"
            : string.Empty;

        return new StatisticalAlertCandidate(
            ElevatedZoneWithoutMovementRule, AlertType.HeartRate, AlertSeverity.Orange,
            "Their heart worked hard on a quiet day",
            $"Yesterday their heart spent about {elevated} minutes in a raised zone{zoneFloor}, "
            + $"on a day of only {yesterday.Steps:N0} steps against a usual "
            + $"{baseline.AvgSteps:N0}. Effort without movement is worth a check-in — how are they "
            + "feeling, and have they been warm or short of breath?",
            Serialize(new
            {
                rule = ElevatedZoneWithoutMovementRule,
                day = yesterday.Date.ToString("O"),
                elevatedZoneMinutes = elevated,
                baselineAvgElevatedZoneMinutes = baseline.AvgElevatedZoneMinutes,
                thresholdMinutes = threshold,
                steps = yesterday.Steps,
                baselineAvgSteps = baseline.AvgSteps,
                moderateZoneFloorBpm = yesterday.ModerateZoneFloorBpm,
            }),
            NightOf: yesterday.Date);
    }

    /// <summary>
    /// One unbroken sedentary stretch far longer than this member's own usual, and past three
    /// hours in absolute terms.
    /// </summary>
    /// <remarks>
    /// The rule the settings catalogue has carried as "Long daytime rest — an unusually long
    /// inactive stretch in waking hours" since before there was data behind it. What makes it
    /// possible now is reading <c>activity-level</c> as intervals rather than as a daily total:
    /// six hours of stillness in twelve half-hours and one unbroken six-hour stretch sum to the
    /// same <c>SedentaryMinutes</c> and are not the same day. Only the second is worth a word.
    /// <para>
    /// Both a floor and a margin, because either alone misreads someone. The floor keeps a member
    /// whose usual longest stretch is forty minutes from being alerted at an hour; the margin
    /// keeps a member who habitually sits for three hours from being alerted every afternoon.
    /// </para>
    /// </remarks>
    public static StatisticalAlertCandidate? DaytimeInactivityBlock(
        PatternBaseline baseline, ActivityLog? yesterday)
    {
        if (yesterday?.LongestSedentaryStretchMinutes is not { } stretch)
            return null;

        var usual = baseline.AvgLongestSedentaryStretchMinutes;
        var threshold = usual is > 0
            ? Math.Max(SedentaryStretchFloorMinutes, (int)(usual.Value * (1 + SedentaryStretchMarginFraction)))
            : SedentaryStretchFloorMinutes;
        if (stretch <= threshold)
            return null;

        var startedAt = yesterday.LongestSedentaryStretchStartUtc is { } start
            ? $", from about {start:HH\\:mm} UTC"
            : string.Empty;
        var usualClause = usual is > 0
            ? $" — their usual longest is about {Hours(usual.Value)}"
            : string.Empty;

        return new StatisticalAlertCandidate(
            DaytimeInactivityBlockRule, AlertType.Inactivity, AlertSeverity.Yellow,
            "A long stretch without moving",
            $"They went about {Hours(stretch)} without moving at all yesterday{startedAt}"
            + $"{usualClause}. A long unbroken rest is not the same as a quiet day — worth asking "
            + "whether they were comfortable, and whether anything kept them in the chair.",
            Serialize(new
            {
                rule = DaytimeInactivityBlockRule,
                day = yesterday.Date.ToString("O"),
                longestSedentaryStretchMinutes = stretch,
                baselineAvgLongestSedentaryStretchMinutes = usual,
                thresholdMinutes = threshold,
                startedAtUtc = yesterday.LongestSedentaryStretchStartUtc?.ToString("O"),
            }),
            NightOf: yesterday.Date);
    }

    /// <summary>Minutes as a plain-language span — "3.5 hours" — for the copy above.</summary>
    private static string Hours(int minutes) =>
        string.Create(CultureInfo.InvariantCulture, $"{minutes / 60m:0.#} hours");

    private static string Serialize(object metrics) => JsonSerializer.Serialize(metrics);
}
