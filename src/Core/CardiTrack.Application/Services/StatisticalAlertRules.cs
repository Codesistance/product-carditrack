using System.Text.Json;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <summary>One rule's verdict: everything the orchestrator needs to write the alert.
/// <see cref="NightOf"/> is set by rules that judge one specific day's data rather than the
/// firing day's — a night (stamped with the civil day it ended on), or an event such as a
/// rhythm notification or a blood-sugar reading. The orchestrator dedups those per stamped day,
/// because late-arriving data can put the same night or event in front of the rule on two
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
    public const string IrregularRhythmRule = "irregular_rhythm";
    public const string EcgAtrialFibrillationRule = "ecg_afib";
    public const string RapidWeightGainRule = "rapid_weight_gain";
    public const string BloodSugarOutOfRangeRule = "blood_sugar_out_of_range";

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
    /// Weight-gain thresholds and the windows they are measured over: 1.4 kg (3 lb) inside three
    /// days, or 2.3 kg (5 lb) inside a week. These are the figures heart-failure self-monitoring
    /// guidance gives patients for daily weighing, and they are about fluid, not fat — nobody puts
    /// on three pounds of tissue in three days, which is exactly what makes the number readable.
    /// </summary>
    public const decimal WeightGainShortWindowKg = 1.4m;
    public const decimal WeightGainLongWindowKg = 2.3m;
    public const int WeightGainShortWindowDays = 3;
    public const int WeightGainLongWindowDays = 7;

    /// <summary>
    /// Blood glucose thresholds in mg/dL, the unit the provider reports in. 70 is the ADA's level-1
    /// hypoglycaemia threshold and 250 the level at which its guidance tells people to check for
    /// ketones. Deliberately wide of the 70–180 target band the dashboard draws: a target band is
    /// what a reading should sit inside, and paging a family every time one strays outside it would
    /// page them several times a day for a person managing their diabetes normally.
    /// </summary>
    public const decimal HypoglycaemiaMgDl = 70m;
    public const decimal HyperglycaemiaMgDl = 250m;

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
    /// The wearable raised one or more irregular-rhythm notifications — its own screening feature
    /// telling the wearer it saw a rhythm that could be atrial fibrillation.
    /// </summary>
    /// <remarks>
    /// CardiTrack judges nothing here and must not appear to: the device made the finding, told the
    /// wearer, and this rule carries it to the family who would otherwise hear about it only if the
    /// wearer thought to mention it. Orange rather than red — this is a screening notification from
    /// an optical sensor, and the reading that can actually be shown to a doctor is the ECG in
    /// <see cref="EcgAtrialFibrillation"/>.
    /// <para>
    /// Reads the freshest day carrying an event, today or yesterday: a notification raised late in
    /// the evening lands on that day's row, and by the time the fifteen-minute pass sees it the
    /// member's own calendar may have rolled over. The day it judged is stamped on the candidate,
    /// so an event alerts once whichever pass gets to it first.
    /// </para>
    /// </remarks>
    public static StatisticalAlertCandidate? IrregularRhythm(ActivityLog? today, ActivityLog? yesterday)
    {
        var day = FirstWith(today, yesterday, l => l.IrregularRhythmNotifications is > 0);
        if (day?.IrregularRhythmNotifications is not { } notifications)
            return null;

        var plural = notifications == 1 ? "a notification" : $"{notifications} notifications";
        return new StatisticalAlertCandidate(
            IrregularRhythmRule, AlertType.Rhythm, AlertSeverity.Orange,
            "Their watch flagged an irregular heart rhythm",
            $"The watch raised {plural} about an irregular heart rhythm. That is the watch's own "
            + "check, not a diagnosis, but it is worth telling their doctor about and worth a call "
            + "to ask how they are feeling.",
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
    /// The strongest single signal this engine can report, and the only red one that is not about
    /// someone being unresponsive: a recorded trace their own device classified, which a clinician
    /// can open and read. Only the <c>ATRIAL_FIBRILLATION</c> classification counts — the
    /// inconclusive and unreadable ones mean the device declined to judge, and the ingestion layer
    /// never counts them here (see <c>GoogleHealthApiDtos</c>).
    /// </remarks>
    public static StatisticalAlertCandidate? EcgAtrialFibrillation(ActivityLog? today, ActivityLog? yesterday)
    {
        var day = FirstWith(today, yesterday, l => l.EcgAtrialFibrillationReadings is > 0);
        if (day?.EcgAtrialFibrillationReadings is not { } readings)
            return null;

        return new StatisticalAlertCandidate(
            EcgAtrialFibrillationRule, AlertType.Rhythm, AlertSeverity.Red,
            "An ECG came back as atrial fibrillation",
            "They took an ECG on their device and it was classified as atrial fibrillation. The "
            + "reading is saved on their device for a doctor to look at — please get in touch with "
            + "them, and with their GP or clinic.",
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
    /// Weight up 1.4 kg inside three days, or 2.3 kg inside a week, against the earliest weighing
    /// in that window.
    /// </summary>
    /// <remarks>
    /// This is a fluid-retention rule wearing a weight rule's clothes. Sudden weight gain over days
    /// is the classic early sign that a failing heart is holding on to fluid, and it is the one
    /// thing on this list a family can see coming days before anyone feels unwell.
    /// <para>
    /// Measured against the earliest reading in the window rather than the lowest: the lowest turns
    /// one unusually light weighing — the scale caught before breakfast, or a day of poor appetite
    /// — into a permanent low-water mark that everything afterwards is a "gain" from. A member who
    /// does not weigh themselves at both ends of a window gets no alert at all, which is correct:
    /// two readings days apart are the entire measurement.
    /// </para>
    /// </remarks>
    public static StatisticalAlertCandidate? RapidWeightGain(
        IReadOnlyDictionary<DateOnly, ActivityLog> logsByDate, DateOnly today)
    {
        var latest = Enumerable.Range(0, 2)
            .Select(offset => logsByDate.GetValueOrDefault(today.AddDays(-offset)))
            .FirstOrDefault(l => l?.WeightKg is not null);
        if (latest?.WeightKg is not { } weight)
            return null;

        foreach (var (windowDays, thresholdKg) in new[]
                 {
                     (WeightGainShortWindowDays, WeightGainShortWindowKg),
                     (WeightGainLongWindowDays, WeightGainLongWindowKg),
                 })
        {
            var earliest = Enumerable.Range(1, windowDays)
                .Select(offset => logsByDate.GetValueOrDefault(latest.Date.AddDays(-offset)))
                .LastOrDefault(l => l?.WeightKg is not null);
            if (earliest?.WeightKg is not { } previousWeight)
                continue;

            var gain = weight - previousWeight;
            if (gain < thresholdKg)
                continue;

            return new StatisticalAlertCandidate(
                RapidWeightGainRule, AlertType.Trend, AlertSeverity.Orange,
                "Their weight has gone up quickly",
                $"Weight is up {gain:0.#} kg since {earliest.Date:d MMMM} — {weight:0.#} kg against "
                + $"{previousWeight:0.#} kg. A rise this fast over a few days is usually fluid rather than "
                + "anything they have eaten, and it is worth mentioning to their doctor.",
                Serialize(new
                {
                    rule = RapidWeightGainRule,
                    day = latest.Date.ToString("O"),
                    weightKg = weight,
                    fromWeightKg = previousWeight,
                    fromDay = earliest.Date.ToString("O"),
                    gainKg = Math.Round(gain, 2),
                    windowDays,
                    thresholdKg,
                }),
                NightOf: latest.Date);
        }

        return null;
    }

    /// <summary>
    /// A blood-sugar reading below 70 mg/dL or above 250 mg/dL — read from the day's lowest and
    /// highest, which is why ingestion keeps both rather than the average alone.
    /// </summary>
    /// <remarks>
    /// Only fires for a member whose readings reach CardiTrack at all, which means a meter or CGM
    /// feeding the same provider account. The low side is red and the high side orange, and the
    /// asymmetry is the point: a high reading is a bad afternoon that needs managing, a low one can
    /// take someone off their feet within the hour, and an older person living alone is exactly who
    /// nobody would find.
    /// </remarks>
    public static StatisticalAlertCandidate? BloodSugarOutOfRange(ActivityLog? today, ActivityLog? yesterday)
    {
        var day = FirstWith(
            today,
            yesterday,
            l => l.BloodGlucoseMin < HypoglycaemiaMgDl || l.BloodGlucoseMax > HyperglycaemiaMgDl);
        if (day is null)
            return null;

        var low = day.BloodGlucoseMin < HypoglycaemiaMgDl ? day.BloodGlucoseMin : null;
        var high = day.BloodGlucoseMax > HyperglycaemiaMgDl ? day.BloodGlucoseMax : null;

        var (severity, title, message) = low is { } lowest
            ? (AlertSeverity.Red,
                "A very low blood sugar reading",
                $"A blood sugar reading of {lowest:0.#} mg/dL came through — below the 70 mg/dL "
                + "mark. Please check on them now, and make sure they have something sugary to hand.")
            : (AlertSeverity.Orange,
                "A very high blood sugar reading",
                $"A blood sugar reading of {high:0.#} mg/dL came through — well above the usual "
                + "target range. Worth a call to see how they are, and worth their doctor knowing.");

        return new StatisticalAlertCandidate(
            BloodSugarOutOfRangeRule, AlertType.PatternBreak, severity, title, message,
            Serialize(new
            {
                rule = BloodSugarOutOfRangeRule,
                day = day.Date.ToString("O"),
                bloodGlucoseMin = day.BloodGlucoseMin,
                bloodGlucoseMax = day.BloodGlucoseMax,
                hypoglycaemiaMgDl = HypoglycaemiaMgDl,
                hyperglycaemiaMgDl = HyperglycaemiaMgDl,
            }),
            NightOf: day.Date);
    }

    /// <summary>
    /// The freshest of today and yesterday that carries the signal a rule is looking for, or null
    /// when neither does. Event rules read two days rather than one because an event landing late
    /// in the member's evening can be read by a pass that has already rolled over to the next
    /// calendar day — and an alert about their heart rhythm must not be lost to that hour.
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
