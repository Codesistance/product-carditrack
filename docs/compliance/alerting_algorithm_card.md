# Alerting algorithm card

**Status:** Matches the code as of 2026-09-06. Companion to [art22_alerting_analysis.md](art22_alerting_analysis.md) and [mathnet_numerics.md](../technical/mathnet_numerics.md). Caregiver-facing summary lives on `/privacy` (“How alerting works”). This card is the Art. 15 artefact: named formulas, named engine, named constants.

CardiTrack computes every number in-process. MedGemma only interprets numbers it is given. It never sets a threshold, never writes an `Alert` by mumbling, and never replaces the rules below.

## 1. What must be true before any statistical alert

| Gate | Rule | Code |
|---|---|---|
| Coverage | A baseline is written only when ≥80% of the window has data (24 of 30 days; 6 of 7 for the shortest provisional window) | `BaselineCalculator.RequiredCoverage` |
| Per-metric floor | Each metric needs 7 samples of its own (scaled to the window’s coverage bar on windows shorter than 9 days); a thin metric is left null | `BaselineCalculator` |
| Established window only | Statistical alerts fetch the **30-day** baseline. 7- and 14-day *provisional* rows colour the dashboard and never page | `StatisticalAlertService` |
| Null ≠ zero | A missing reading is “not measured”, not “did nothing”. The one red no-morning rule requires a **measured** zero steps | `StatisticalAlertRules` |
| The nine rules below are not tunable | Only the hard-coded “medium” profile exists (30%). `AlertSensitivity` (low/high) is still stored and still unused by every producer. Per-CardiMember **rule enablement** (on by default; off skips evaluation) is separate — see `AlertPreference` / `AlertRuleCatalogue` | `StatisticalAlertRules.DeviationFraction`; `AlertPreferenceService` |
| Caregiver-defined alarms are a **separate producer**, not a retuning of these rules | `MetricAlarm` (R2) evaluates thresholds a caregiver set themselves and writes `Alert` rows stamped `rule: "custom:{alarmId}"`. It cannot change any threshold in §2, and it is bounded by its own controls — see §2a | `MetricAlarmEvaluator`; [alarm_catalogue.md](../technical/alarm_catalogue.md) |

Mean and sample σ (n−1) are computed in `BaselineCalculator` (package-free Application). Median and unscaled MAD are computed via `IDescriptiveStatistics` (Math.NET in Infrastructure) and **persisted on the same `PatternBaseline` row**. Live R1 rules still threshold on the mean / σ. Median/MAD exist so G2 (MAD/IQR fences for steps and sleep) can be shadow-evaluated without retuning production.

## 2. Statistical rules (R1) — Worker, every 15 minutes

| Rule | Fires when | Severity | Constant |
|---|---|---|---|
| Activity decline | Yesterday’s steps &lt; 70% of the 30-day mean (`AvgSteps`) | Yellow | 30% of mean |
| Irregular sleep | Last night’s sleep minutes more than 30% above or below `AvgSleepMinutes`, either direction | Yellow | 30% of mean |
| Elevated resting HR | Yesterday’s resting HR &gt; mean + max(2σ, 5 bpm) | Orange | 2σ, 5 bpm floor |
| No morning activity | Today’s steps are a **measured 0**, and local time is ≥ typical wake + 2 hours | Red | 2-hour grace |
| Long-term trend | Four consecutive weeks each ≥5% below the previous week’s average steps (week needs ≥4 days with a reading) | Yellow | 5%/week × 4 |
| HRV drop | Overnight RMSSD below `AvgHeartRateVariabilityMs` − max(2σ, 15% of the mean) on **both** of the last two nights | Orange | 2σ, 15%-of-mean floor, 2 nights |
| Overnight breathing up | Last night’s *asleep* breathing rate above `AvgOvernightBreathingRate` + max(2σ, 1 breath/min) | Orange | 2σ, 1/min floor |
| Elevated zone without movement | Minutes above the light HR zone &gt; max(`AvgElevatedZoneMinutes`, 25) **and** the same day already satisfies activity decline | Orange | 25-minute floor + the decline rule |
| Long daytime rest | One unbroken sedentary stretch &gt; max(3 h, `AvgLongestSedentaryStretchMinutes` + 50%) | Yellow | 3-hour floor, +50% margin |

Three of the four rules added on 2026-08-22 threshold on data types CardiTrack did not previously read (`daily-heart-rate-variability`, `respiratory-rate-sleep-summary`, `time-in-heart-rate-zone`, `activity-level`); see [llm_design.md](../llm_design.md) for the mapping. Two carry a floor as well as a σ margin, and the floors are not interchangeable in kind: HRV’s is **proportional** (15% of the member’s own mean) because overnight RMSSD is not comparable between people — a healthy 80-year-old may sit near 15 ms where a healthy 40-year-old sits near 60 — while breathing’s is **absolute** (1 breath/min) because every adult sits in the low-to-mid teens asleep and a rise of one means the same thing at 13 as at 17.

**Long daytime rest excludes the night.** The stretch is measured from `activity-level` intervals with the night’s own sleep session clipped out before the longest run is taken. Without that exclusion a sleeping wearer — who is a sedentary wearer — makes the small hours the longest unbroken run on nearly every day, and the rule would report sleep as daytime rest.

**Elevated zone without movement is a pairing, not a threshold.** Raised-zone minutes after a walk are what exercise looks like; the finding is those minutes on a day the activity-decline rule already calls quiet. It reuses that rule rather than restating its threshold, so the two cannot disagree about what a quiet day is.

Bedtime / wake time on the baseline are a **circular mean** on the 24-hour clock (UTC as stored). There is no Math.NET circular clock-mean; that formula stays homemade.

## 2a. Caregiver-defined alarms (R2) — Worker, every five minutes

A second deterministic producer, and the only one whose numbers a user chooses. It does not touch §2.

| Property | Rule | Code |
|---|---|---|
| Grammar | metric · statistic · comparison · threshold · period · M-of-N datapoints · missing-data treatment · severity | `MetricAlarm` |
| Threshold kinds | A fixed level, a share of the member's baseline average, or a multiple of their baseline σ | `AlarmThresholdKind` |
| Established window only | Baseline-relative kinds resolve against the **30-day** row alone; no established baseline means `InsufficientData` and no alert | `MetricAlarmEvaluator.ResolveThreshold` |
| Null ≠ zero | Three missing-data verbs. CloudWatch's `breaching` — absence counts as a breach — is **deliberately absent**, because it would convert an unworn watch into a page and contradicts the gate in §1 | `AlarmMissingDataTreatment` |
| One alert per episode | An alert is written on the **transition** into alarm and not while it stands; a return to normal re-arms it. The alert lifecycle deliberately does not — acknowledging a card is not a statement that the reading has come down | `MetricAlarmState` |
| Hysteresis | A standing alarm clears only 5% inside its firing threshold, so a value on the line does not page on every crossing | `MetricAlarmEvaluator.HysteresisFraction` |
| Bounded thresholds | Each metric's absolute threshold is clamped to a band. The clinical bradycardia line (60 bpm) sits inside the range a caregiver could otherwise enter, and would fire nightly on an ordinary sleeping heart | `AlarmMetricCatalogue` |
| Bounded count | At most 12 enabled alarms per member; the client advises past 6 | `MetricAlarmValidation` |
| Deliberate confirmation for Red | A red alarm pushes through quiet hours and escalates; saving one requires an explicit acknowledgement | `SaveMetricAlarmRequest.ConfirmCriticalSeverity` |
| Evidence | Every alert carries its metric, configured and effective thresholds, observed value, window, and how many datapoints breached | `MetricAlarmEngine.BuildAlert` |

The suggested defaults, the published guidance behind each number, and the places where sources
disagree or a figure is ours are documented in
[alarm_catalogue.md](../technical/alarm_catalogue.md). Suggested alarms are **prefilled in the
builder, never auto-enabled**.

## 3. Real-time heart-rate hour (AI pipeline, dev)

| Step | What happens | Named engine / model |
|---|---|---|
| Window | Latest 60 minutes of granular HR; skip if fewer than 45 minutes have data | — |
| Decompose | Broomhead–King lag-covariance SSA, L=30; component 0 = trend, 1–2 = oscillation, residual = noise | `SsaParameters.Engine = "MathNet.Numerics.Evd"` |
| Yardstick | `HrDeviationScore = \|last − trend\| / max(noise RMS, 0.5 bpm)` | `RealtimeAssessmentService.NoiseFloorBpm` |
| Interpret | MedGemma writes 1–3 sentences and a severity word. It is not given a numeric risk score | MedGemma (`Q4_K_M`) |
| Route | Only `critical`/`high` (red/orange) become an `Alert`. Unparseable output is stored with null severity and never alerts | `AssessmentSeverityParser` |
| Stamp | Each row records `SsaEngine` so two solvers cannot be mixed inside the 90-day partition | `RealtimeAssessment.SsaEngine` |

The model cannot invent a number. Contestability is: stored features + named engine + stored prompt output.

## 4. Device silence (Worker)

No granular reading for &gt;2 hours during the member’s local waking hours (default 07:00–22:00). Yellow `Inactivity` alert. Rule-based text; no AI call.

## 5. What this card deliberately does not claim

- Low / high sensitivity **for the rules in §2** — still unbuilt, and `AlertSensitivity` still drives nothing. Per-member *caregiver-defined* thresholds now exist as a separate producer (§2a); they add alarms beside these rules and cannot retune them.
- Median / MAD / IQR as live alert fences — persisted, shadow-evaluated, **not** what pages a family.
- Mann–Kendall, W-correlation grouping, or ML.NET TimeSeries spike detection — increment 4, not shipped.
- That MedGemma computes or replaces any of the above.

## 6. Change control

A change to a threshold, a baseline formula (mean/σ vs robust location), the SSA engine string, a `CARDITRACK_*` prompt, or the model tag is an Art. 22 V4 event and a DPIA §13 review trigger. So is a change to **§2a's evaluator arithmetic, its threshold bounds, or its suggested defaults** — a caregiver choosing the number does not make the machinery that acts on it unreviewable. Record it in [art22_alerting_analysis.md](art22_alerting_analysis.md) §5 before mixing pre/post rows in a V2 claim.
