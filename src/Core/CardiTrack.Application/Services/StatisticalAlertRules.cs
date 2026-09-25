using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <summary>
/// One rule's finding: what was measured, what is usual for this member, and the yardstick
/// that made the reading worth a judgement. Not a verdict — the severity a family is shown and
/// the words they read come from the medical model, which is handed these findings as its
/// input (<c>StatisticalAlertService</c>). <see cref="Observation"/> is written for that model,
/// in figures, and reaches no caregiver.
/// <para>
/// <see cref="NightOf"/> is set by rules that judge one specific night (the civil day the night
/// ended on) rather than the firing day's data — the orchestrator dedups those per night,
/// because late-arriving data can put the same night in front of the rule on two calendar days.
/// </para>
/// </summary>
public sealed record StatisticalFinding(
    string Rule, AlertType Type, string Observation, string MetricValues, DateOnly? NightOf = null);

/// <summary>
/// The R1 statistical rules (docs/execution/backend/api/alerts.md taxonomy) — pure functions
/// from baseline + daily readings to a <see cref="StatisticalFinding"/> or null, deliberately
/// free of I/O so every threshold is unit-testable to its boundary. Thresholds are the
/// hard-coded "medium" sensitivity profile (deviation &gt; 30%); the low/high profiles wait on
/// wiring <c>CardiMember.AlertSensitivity</c>. Per-rule on/off lives in <c>AlertPreference</c>.
/// <para>
/// <b>These rules are an input provider, not the inference.</b> A rule says a reading crossed a
/// yardstick and states the figures; whether that is worth the family's attention, how much, and
/// in what words is the medical model's to decide (docs/llm_design.md: deterministic code
/// computes every number, MedGemma only ever interprets them). Until 2026-09-19 each rule
/// carried its own severity, title and message straight into the alert row, so a threshold
/// constant was paging families with no model in the loop; that is the breach this shape closes.
/// </para>
/// <para>
/// Every <b>comparative</b> rule takes the <b>established 30-day</b> baseline only — provisional
/// 7/14-day baselines never alert (a statistically thin window would trade the &lt;5%
/// false-positive target for early noise), which the orchestrator enforces by what it fetches.
/// The two <b>measured</b> rules (<see cref="IrregularRhythmRule"/>, <see cref="EcgAtrialFibrillationRule"/>)
/// relay a finding the wearer's device already classified, so they need no baseline and run
/// without one.
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
    public const string IrregularRhythmRule = "irregular_rhythm";
    public const string EcgAtrialFibrillationRule = "ecg_afib";
    public const string OvernightBreathingUpRule = "overnight_breathing_up";
    public const string ElevatedZoneWithoutMovementRule = "elevated_zone_without_movement";
    public const string DaytimeInactivityBlockRule = "daytime_inactivity_block";

    /// <summary>
    /// Every rule this class can produce a finding for. The judgement reply's <c>rule</c> field
    /// carries the same eleven as an <c>[AllowedValues]</c> enum, which has to list them one by one
    /// because an attribute takes compile-time constants; this list is what lets a test prove the
    /// two agree, so a twelfth rule cannot be added and then silently be unnameable by the model
    /// asked to judge it.
    /// </summary>
    public static readonly IReadOnlyList<string> AllRules =
    [
        ActivityDeclineRule,
        IrregularSleepRule,
        ElevatedHeartRateRule,
        NoMorningActivityRule,
        LongTermTrendRule,
        HeartRateVariabilityDropRule,
        IrregularRhythmRule,
        EcgAtrialFibrillationRule,
        OvernightBreathingUpRule,
        ElevatedZoneWithoutMovementRule,
        DaytimeInactivityBlockRule,
    ];

    /// <summary>
    /// The key a benign verdict on this finding is remembered under: a hash of the question the
    /// model was actually asked — its rule, the observation it reads, and the figures behind it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A verdict is about the readings as they stand, and for this engine the readings keep
    /// arriving — which is why, before this existed, nothing was persisted at all: judging again
    /// on the next pass is how a day that gets worse gets noticed. The cost was that a yardstick
    /// which stays tripped asks the model the same question every five minutes until midnight, up
    /// to 288 times.
    /// </para>
    /// <para>
    /// Hashing the question itself keeps both. Identical figures produce the same fingerprint and
    /// the remembered verdict stands; one figure moving produces a different fingerprint and the
    /// finding is judged again exactly as it was before. That is a stronger guarantee than the
    /// first shape of this, which remembered a rule for a member's local day on the reasoning that
    /// a rule reading yesterday reads data that cannot change again — false, because
    /// <c>DeviceSyncService</c>'s repair pass re-pulls <c>SyncLookbackDays</c> of complete days and
    /// a night's readings routinely land after local midnight.
    /// </para>
    /// <para>
    /// It also removes the need to sort rules into ones whose data is settled and ones whose data
    /// is not — a list that had to be maintained by hand and that a rule added later could default
    /// into wrongly. <see cref="NoMorningActivity"/> names the clock in its observation and so
    /// re-judges every pass without being named anywhere as an exception; the two measured rules
    /// carry the device's own counts in their figures, so a notification arriving this afternoon
    /// changes the fingerprint on its own.
    /// </para>
    /// </remarks>
    public static string JudgementFingerprint(StatisticalFinding finding)
    {
        var question = string.Join('\n', finding.Rule, finding.Observation, finding.MetricValues);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(question)));
    }

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
    public static StatisticalFinding? ActivityDecline(PatternBaseline baseline, ActivityLog? yesterday)
    {
        if (baseline.AvgSteps is not > 0 || yesterday?.Steps is not { } steps)
            return null;

        var average = baseline.AvgSteps.Value;
        if (steps >= average * (1 - DeviationFraction))
            return null;

        return new StatisticalFinding(
            ActivityDeclineRule, AlertType.Inactivity,
            $"Steps on {Day(yesterday.Date)}: {steps:N0}, against a usual {average:N0} a day. "
            + $"The yardstick is a day more than {DeviationFraction:P0} below their usual.",
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
    /// passes the freshest log that carries a sleep reading. The finding names the night it
    /// judged (<see cref="StatisticalFinding.NightOf"/>) so one night alerts at most once
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
    /// screen whose job is to say what needs attention now. It stays a finding in the one
    /// direction where more sleep is worth flagging — past the recommended ceiling at their age.
    /// A shorter night is always a finding whatever the absolute figure, because a sudden loss of
    /// a third of someone's sleep is a pattern break in its own right.
    /// </para>
    /// </summary>
    /// <param name="ageYears">
    /// The member's age, for the published band the night is graded against — see
    /// <see cref="HealthReferenceRanges.Sleep"/>. Only the ceiling moves with it, and the ceiling
    /// is exactly what decides whether a longer night is a concern, so a default here would quietly
    /// grant every older adult an hour of oversleep the recommendation does not give them.
    /// </param>
    public static StatisticalFinding? IrregularSleep(
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

        // Where the night landed against the recommendation, which is the fact the deviation
        // from their own usual leaves the model to weigh. Past the guard above, a longer night is
        // necessarily one that overshot the ceiling.
        var band = longer
            ? $" and past the {recommended.High:0.#}-hour ceiling recommended at their age"
            : $"; the recommended range at their age is {recommended.Low:0.#} to {recommended.High:0.#} hours";

        // An awake night is named as one: "0 hours" reads as a watch that measured nothing, and
        // the status has established the opposite — worn through, no sleep recorded.
        var night = lastNight.NightStatus == NightSleepStatus.Awake
            ? $"{ReadingFigures.AwakeNight}, 0 hours"
            : $"{hours:0.#} hours";

        return new StatisticalFinding(
            IrregularSleepRule, AlertType.Sleep,
            $"Sleep on the night ending {Day(lastNight.Date)}: {night}, "
            + $"{(longer ? "more" : "less")} than the usual {usualHours:0.#}{band}. "
            + $"The yardstick is a night more than {DeviationFraction:P0} off their usual; a longer "
            + "night counts only past the recommended ceiling.",
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
    public static StatisticalFinding? ElevatedHeartRate(PatternBaseline baseline, ActivityLog? yesterday)
    {
        if (baseline.AvgRestingHeartRate is not > 0 || yesterday?.RestingHeartRate is not { } restingHr)
            return null;

        var average = baseline.AvgRestingHeartRate.Value;
        var margin = Math.Max(
            HrSigmaMultiplier * (double)(baseline.StdDevHeartRate ?? 0), HrMarginFloorBpm);
        if (restingHr <= average + margin)
            return null;

        return new StatisticalFinding(
            ElevatedHeartRateRule, AlertType.HeartRate,
            $"Resting heart rate on {Day(yesterday.Date)}: {restingHr} bpm, against a usual {average} bpm. "
            + $"The yardstick is their usual plus {margin:0.#} bpm (the larger of two standard "
            + $"deviations and {HrMarginFloorBpm} bpm).",
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
    public static StatisticalFinding? NoMorningActivity(
        PatternBaseline baseline, ActivityLog? today, DateTime localNow)
    {
        if (baseline.TypicalWakeTime is not { } wake || today?.Steps is not 0)
            return null;

        var earliest = wake.ToTimeSpan().Add(TimeSpan.FromHours(MorningGraceHours));
        if (localNow.TimeOfDay < earliest)
            return null;

        return new StatisticalFinding(
            NoMorningActivityRule, AlertType.PatternBreak,
            $"Steps recorded so far today: a measured zero, with the device reporting. The time is "
            + $"{localNow:HH\\:mm} local against a typical waking time of {wake:HH\\:mm}. The yardstick "
            + $"is {MorningGraceHours} hours past their usual waking time with no movement recorded.",
            Serialize(new { rule = NoMorningActivityRule, typicalWakeTime = wake.ToString("HH:mm") }));
    }

    /// <summary>
    /// Weekly step averages declining ≥5% week-over-week for 4 consecutive weeks (ending
    /// yesterday). Each week needs enough measured days for its average to mean anything.
    /// </summary>
    public static StatisticalFinding? LongTermTrend(
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
        var oldestFirst = string.Join(", ", weeklyAverages.Reverse().Select(a => Math.Round(a).ToString("N0", CultureInfo.InvariantCulture)));
        return new StatisticalFinding(
            LongTermTrendRule, AlertType.Trend,
            $"Average daily steps over the last {TrendWeeks} weeks ending {Day(yesterday)}, oldest week "
            + $"first: {oldestFirst}. Each week sat at least {WeeklyDeclineFraction:P0} below the one "
            + $"before it, about {totalDecline:P0} lower over the whole stretch. The yardstick is that "
            + "many consecutive weeks of decline.",
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
    public static StatisticalFinding? HeartRateVariabilityDrop(
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

        return new StatisticalFinding(
            HeartRateVariabilityDropRule, AlertType.HeartRate,
            $"Overnight heart rate variability on the nights ending {Day(previousNight.Date)} and "
            + $"{Day(lastNight.Date)}: {previous:0.#} ms and {latest:0.#} ms, against a usual "
            + $"{average:0.#} ms. The yardstick is two consecutive nights below their usual minus "
            + $"{margin:0.#} ms.",
            Serialize(new
            {
                rule = HeartRateVariabilityDropRule,
                day = lastNight.Date.ToString("O"),
                // The key AlertRuleMarkers.HasNight reads. `day` alone left every rule below
                // deduping per firing day, so a night whose data landed after local midnight could
                // alert twice — the exact case NightOf exists to prevent.
                night = lastNight.Date.ToString("O"),
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
    public static StatisticalFinding? OvernightBreathingUp(
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

        // No published band in the observation and none in the stamp. The only adult range this
        // codebase holds is WHO's respiratory rate at rest, and this reading is measured across
        // hours of sleep — the sentence here used to call it "the published typical adult range
        // asleep", which is a claim no body makes. Saying it in the prompt was the worst place to
        // say it: this text is what MedGemma judges the finding on, so the model was handed a
        // yardstick for a measurement it was not looking at and told it was published. Their own
        // usual plus their own margin is the whole comparison, and it is enough — see
        // HealthReferenceRanges.NoOvernightBreathingBand.
        return new StatisticalFinding(
            OvernightBreathingUpRule, AlertType.PatternBreak,
            $"Breathing rate asleep on the night ending {Day(lastNight.Date)}: {breathing:0.#} a minute, "
            + $"against a usual {average:0.#}. The yardstick is their usual plus {margin:0.#} a minute. "
            + "There is no published adult range for breathing measured asleep, so this is judged "
            + "against their own usual alone.",
            Serialize(new
            {
                rule = OvernightBreathingUpRule,
                day = lastNight.Date.ToString("O"),
                night = lastNight.Date.ToString("O"),
                overnightBreathingRate = breathing,
                baselineAvgOvernightBreathingRate = average,
                marginPerMinute = Math.Round(margin, 1),
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
    public static StatisticalFinding? ElevatedZoneWithoutMovement(
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
            ? $" (above {floor} bpm, where their watch puts the start of real effort)"
            : string.Empty;

        return new StatisticalFinding(
            ElevatedZoneWithoutMovementRule, AlertType.HeartRate,
            $"On {Day(yesterday.Date)}: {elevated} minutes above the light heart-rate zone{zoneFloor}, "
            + $"on {yesterday.Steps:N0} steps against a usual {baseline.AvgSteps:N0}. The yardstick "
            + $"is more than {threshold} raised minutes on a day the steps already count as a decline "
            + "— the pairing is the finding, not either half.",
            Serialize(new
            {
                rule = ElevatedZoneWithoutMovementRule,
                day = yesterday.Date.ToString("O"),
                night = yesterday.Date.ToString("O"),
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
    public static StatisticalFinding? DaytimeInactivityBlock(
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

        var usualClause = usual is > 0
            ? $", against a usual longest of about {Hours(usual.Value)}"
            : string.Empty;

        return new StatisticalFinding(
            DaytimeInactivityBlockRule, AlertType.Inactivity,
            // No clock time here: the rules layer has no timezone, so any time it named would be
            // UTC. The instant stays in the metrics for the detail screen to localise.
            $"Longest unbroken still stretch in waking hours on {Day(yesterday.Date)}: {Hours(stretch)}"
            + $"{usualClause}. The yardstick is more than {Hours(threshold)}.",
            Serialize(new
            {
                rule = DaytimeInactivityBlockRule,
                day = yesterday.Date.ToString("O"),
                night = yesterday.Date.ToString("O"),
                longestSedentaryStretchMinutes = stretch,
                baselineAvgLongestSedentaryStretchMinutes = usual,
                thresholdMinutes = threshold,
                startedAtUtc = yesterday.LongestSedentaryStretchStartUtc?.ToString("O"),
            }),
            NightOf: yesterday.Date);
    }

    /// <summary>Minutes as a plain-language span — "3.5 hours" — for the observations above.</summary>
    private static string Hours(int minutes) =>
        string.Create(CultureInfo.InvariantCulture, $"{minutes / 60m:0.#} hours");

    /// <summary>
    /// The civil day an observation is about, as an ISO date. Never a relative word: the model
    /// is told the date so it can weigh the finding, and told separately not to carry a
    /// "yesterday" into copy that will be read on other days.
    /// </summary>
    private static string Day(DateOnly date) => date.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// The wearable raised one or more irregular-rhythm notifications — its own screening feature
    /// telling the wearer it saw a rhythm that could be atrial fibrillation.
    /// </summary>
    /// <remarks>
    /// A <b>measured</b> rule: it reports a finding the device itself made, so unlike every
    /// comparative rule here it takes no baseline and must not be gated on one. A member two weeks
    /// into wearing a watch has no established baseline and exactly the same heart.
    /// <para>
    /// Reads the freshest day carrying an event, today or yesterday: a notification raised late in
    /// the evening lands on that day's row, and by the time the fifteen-minute pass sees it the
    /// member's own calendar may have rolled over. The day it judged is stamped on the finding, so
    /// an event is reported once whichever pass gets to it first.
    /// </para>
    /// <para>
    /// The observation says who made the finding, because the model writes the family's words from
    /// it and the distinction is the whole point: the watch screened and flagged, CardiTrack did
    /// not, and nothing here is a diagnosis.
    /// </para>
    /// </remarks>
    public static StatisticalFinding? IrregularRhythm(ActivityLog? today, ActivityLog? yesterday)
    {
        var day = FirstWith(today, yesterday, l => l.IrregularRhythmNotifications is > 0);
        if (day?.IrregularRhythmNotifications is not { } notifications)
            return null;

        return new StatisticalFinding(
            IrregularRhythmRule, AlertType.Rhythm,
            $"On {Day(day.Date)} the wearer's own device raised {notifications} irregular-rhythm "
            + "notification(s): its optical screening feature detected a rhythm consistent with "
            + "atrial fibrillation and told the wearer so. This is the device's finding, not a "
            + "CardiTrack one, and it is a screening result rather than a diagnosis.",
            Serialize(new
            {
                rule = IrregularRhythmRule,
                day = day.Date.ToString("O"),
                irregularRhythmNotifications = notifications,
            }),
            NightOf: day.Date);
    }

    /// <summary>
    /// An ECG the wearer recorded on their device came back classified as atrial fibrillation.
    /// </summary>
    /// <remarks>
    /// Measured, like <see cref="IrregularRhythm"/>, and for the same reason takes no baseline.
    /// Only the <c>ATRIAL_FIBRILLATION</c> classification reaches this column — the inconclusive
    /// and unreadable ones mean the device declined to judge, and the ingestion layer never counts
    /// them (see <c>GoogleHealthApiClient</c>).
    /// <para>
    /// Unlike the notification above, this one has a trace behind it a clinician can open on the
    /// wearer's device. The observation says so, because it is the difference between "worth
    /// mentioning" and "there is a recording to show someone".
    /// </para>
    /// </remarks>
    public static StatisticalFinding? EcgAtrialFibrillation(ActivityLog? today, ActivityLog? yesterday)
    {
        var day = FirstWith(today, yesterday, l => l.EcgAtrialFibrillationReadings is > 0);
        if (day?.EcgAtrialFibrillationReadings is not { } readings)
            return null;

        var taken = day.EcgReadings is { } total ? $" out of {total} taken that day" : string.Empty;

        return new StatisticalFinding(
            EcgAtrialFibrillationRule, AlertType.Rhythm,
            $"On {Day(day.Date)} the wearer recorded {readings} ECG reading(s){taken} that their "
            + "device classified as atrial fibrillation. The device made this classification, not "
            + "CardiTrack. The trace itself is saved on the wearer's own device and can be shown "
            + "to a clinician.",
            Serialize(new
            {
                rule = EcgAtrialFibrillationRule,
                day = day.Date.ToString("O"),
                ecgAtrialFibrillationReadings = readings,
                ecgReadings = day.EcgReadings,
            }),
            NightOf: day.Date);
    }

    /// <summary>
    /// The fresher of two days that carries a signal, or null when neither does. Measured rules
    /// use it because their event belongs to the day the device stamped it, not to the day the
    /// pass happened to run.
    /// </summary>
    private static ActivityLog? FirstWith(
        ActivityLog? today, ActivityLog? yesterday, Func<ActivityLog, bool> carriesSignal)
    {
        if (today is not null && carriesSignal(today))
            return today;

        return yesterday is not null && carriesSignal(yesterday) ? yesterday : null;
    }

    private static string Serialize(object metrics) => JsonSerializer.Serialize(metrics);
}
