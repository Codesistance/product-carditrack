# Alarm catalogue — the suggested defaults, and where their numbers come from

**Status: reference for the user-defined alarm feature (`MetricAlarm`, R2).** Companion to
[alerting_algorithm_card.md](../compliance/alerting_algorithm_card.md), which covers the nine rules
CardiTrack runs on its own. This document covers the alarms a **caregiver** sets, the numbers we
suggest as starting points, and the published guidance behind each one.

> ### Verification status — read before quoting this document
>
> **The numbers below are cross-corroborated and high confidence. The wording is not verified.**
> The research behind this catalogue was gathered in an environment whose egress policy blocks
> `heart.org`, `fda.gov`, `mayoclinic.org`, `support.apple.com` and several others, so every source
> was reached through search-engine retrieval rather than by fetching the page. Thresholds, mmHg
> figures and bpm figures agreed across multiple independent restatements; exact sentences did not
> get the same treatment.
>
> **Before any sentence here is quoted externally — in marketing, in a regulatory submission, or to
> a reviewer — open the URL and check it character for character.** Nothing in this document is
> medical, legal or regulatory advice.

---

## 1. What CardiTrack can and cannot alarm on

An alarm can only watch a metric CardiTrack stores. The evaluable set is declared in
`AlarmMetricCatalogue` and served to clients at `GET /api/v1/alarms/catalogue`.

**Not available, and not a near-term gap to be worked around:**

| Missing | Why it matters | Why it is not here |
|---|---|---|
| **Blood pressure** (systolic/diastolic) | The strongest public guidance of any metric researched — the 2025 AHA/ACC categories are unambiguous, and ≥180/120 is a named emergency | No systolic or diastolic column exists anywhere in `CardiTrack.Domain`. No connected engine supplies one; a cuff would need its own ingestion path, its own consent story, and the first paired-value reading in the schema |
| **Weight** | The AHA/AAHFN heart-failure rule (2–3 lb in a day, 5 lb in a week) is well known to caregivers and easy to act on | No weight column. `Weight` appears only as a *device-capability label* in the mobile device picker — we advertise that a device produces it and never ingest it |
| **Irregular rhythm / AFib flag** | Would let a caregiver set a notification burden they can live with | No rhythm flag is ingested from any engine |

Adding any of these is an ingestion change, not an alarm change. Until then, an alarm catalogue that
listed them would be describing a product we do not have.

---

## 2. The suggested alarms

These ship as **prefilled suggestions in the builder, not as auto-enabled alarms**. Nothing here is
switched on for a caregiver who did not choose it — see §5.

| Alarm | Condition | Datapoints | Gate | Severity | Basis |
|---|---|---|---|---|---|
| High heart rate, at rest | `Avg(HeartRate) > 120 bpm` over 5 min | 2 of 2 | still | Orange | **Product default**, not clinical — see §3.1 |
| Low heart rate, at rest | `Avg(HeartRate) < 40 bpm` over 5 min | 2 of 2 | still | Orange | **Product default** — see §3.1 |
| Resting heart rate high | `RestingHeartRate > 100 bpm` daily | 2 of 3 | — | Orange | AHA tachycardia definition (§3.1) |
| Resting heart rate drifting up | `RestingHeartRate >` baseline + 2σ, daily | 2 of 3 | — | Yellow | Personal-baseline method (§3.4) |
| Blood oxygen severe | `Avg(SpO2) < 90%` over 5 min | 3 of 3 | — | Red | WHO severe hypoxaemia (§3.2) |
| Blood oxygen low | `Avg(SpO2) < 92%` over 10 min | 2 of 3 | — | Orange | Consumer clinical guidance (§3.2) |
| Heart rate variability suppressed | `OvernightHRV < 70%` of baseline, nightly | 2 of 3 | — | Yellow | No published band exists (§3.3) |

Three cross-cutting controls apply to every alarm, suggested or not, and they are the load-bearing
part of this design rather than a refinement of it — see §4.

---

## 3. Where each number comes from

### 3.1 Heart rate

| Organisation | Position | Source |
|---|---|---|
| American Heart Association | Normal adult resting heart rate is 60–100 bpm | https://www.heart.org/en/health-topics/high-blood-pressure/the-facts-about-high-blood-pressure/all-about-heart-rate-pulse |
| American Heart Association | Tachycardia is a resting rate over 100 bpm | https://www.heart.org/en/health-topics/arrhythmia/about-arrhythmia/tachycardia--fast-heart-rate |
| American Heart Association | Bradycardia is an adult resting rate under 60 bpm; the AHA notes the rate may fall below 60 during sleep, and in athletes and physically active adults | https://www.heart.org/en/health-topics/arrhythmia/about-arrhythmia/bradycardia--slow-heart-rate |
| Mayo Clinic | Tachycardia is a heart rate over 100 beats a minute | https://www.mayoclinic.org/diseases-conditions/tachycardia/symptoms-causes/syc-20355127 |
| Cleveland Clinic | Same 60–100 band, with the athlete and sleep exceptions | https://my.clevelandclinic.org/health/diagnostics/heart-rate |
| Apple | High/low heart-rate notifications default to **above 120** or **below 40 bpm** after ~10 minutes of apparent inactivity; the high threshold is selectable 100–150 and the low 40–50 | https://support.apple.com/en-us/120276 |
| Fitbit / Google | Same shape — notification when the rate is outside a high or low threshold while apparently inactive for at least 10 minutes; default range 44–120 | https://support.google.com/fitbit/answer/14237938 |

**Our low-heart-rate floor deliberately diverges from the clinical definition, and this is the
single most important disagreement in this document.** Every clinical source puts bradycardia at
under 60 bpm. Every consumer vendor puts the *alarm* floor at 40–50. Both are right about different
questions: 60 is where a clinician starts asking why, and 40 is where a caregiver should be woken.
A CardiTrack alarm set at 60 would fire on an ordinary sleeping heart most nights of the week, and
the caregiver would switch alarms off — which is the outcome §4 exists to prevent. We follow the
vendors, and the threshold field is bounded so a caregiver cannot enter 60 by accident.

**The stillness gate is the real content of the two short-window heart alarms, not the number.**
120 bpm on a staircase is a heart doing its job. Both Apple and Fitbit gate on ~10 minutes of
apparent inactivity, and CardiTrack's `AlarmContextGate.Inactive` is the same idea: a period counts
only when the step series for those same minutes was measured and was zero. Where no step series
exists, no period passes the gate — stillness has to be established, not assumed.

### 3.2 Blood oxygen

| Organisation | Position | Source |
|---|---|---|
| WHO | Normal saturation 94–100% at sea level; 90–93% hypoxaemia; below 90% severe hypoxaemia. Already encoded in `HealthReferenceRanges.SpO2` | WHO pulse oximetry guidance |
| Cleveland Clinic | Normal 95–100% for all ages; contact a provider at 92% or below; seek help immediately at 88% or below | https://my.clevelandclinic.org/health/diagnostics/22447-blood-oxygen-level |
| **FDA** | A cleared pulse oximeter reading 90% generally corresponds to a true saturation between **86 and 94%**, and decisions should be based on **trends over time rather than absolute thresholds** | https://www.fda.gov/medical-devices/safety-communications/pulse-oximeter-accuracy-and-limitations-fda-safety-communication |
| FDA | Accuracy differs between dark and light skin pigmentation; the difference is typically small above 80% saturation and larger below it | https://www.fda.gov/media/175828/download |
| Peer-reviewed | A_RMS 4.15% for dark pigmentation against 1.97% for light — the bias, quantified | https://www.ncbi.nlm.nih.gov/pmc/articles/PMC10879215/ |

**The FDA's position is the strongest external justification for the whole M-of-N design.** If a
displayed 90% carries a ±4-point true range, a single-sample alarm at 90% is defensibly wrong and a
three-sample one is defensibly right. Consumer wearable SpO2 is generally **not** an FDA-cleared
oximeter, so that error band is a floor rather than a ceiling. Any SpO2 alarm CardiTrack offers must
require multiple datapoints, and the measurement caveat belongs at the point of the alarm rather
than buried in terms of use.

### 3.3 Heart rate variability

| Source | Position | URL |
|---|---|---|
| Consumer-wearable HRV review | Age, sex, fitness, smoking, stress and medication all affect HRV, so a normal value varies substantially between individuals | https://www.ncbi.nlm.nih.gov/pmc/articles/PMC10742885/ |
| Longitudinal wearable cohort study | Resting HRV varies widely across individuals and declines with age independently of fitness | https://www.ncbi.nlm.nih.gov/pmc/articles/PMC12693838/ |
| Oura | Builds an individualised baseline and compares against a 28-day rolling average rather than a fixed value | https://support.ouraring.com/hc/en-us/articles/360025588793-Resting-Heart-Rate |
| Validation study | Reliable nocturnal HRV needs a per-segment validity gate and aggregation over ≥30-minute windows | https://www.ncbi.nlm.nih.gov/pmc/articles/PMC11644394/ |
| Quer *et al.*, Nature Medicine 2020 | Deviation from an individual's own baseline in resting heart rate and sleep improved detection over population thresholds | https://www.nature.com/articles/s41591-020-1123-x |
| Alavi *et al.*, Nature Medicine 2021 | Real-time deviation-from-baseline alerting from wearables — the closest published analogue to this feature | https://www.nature.com/articles/s41591-021-01593-2 |

CardiTrack already takes this position in code: `HealthReferenceRanges.NoHeartRateVariabilityBand`
declines to publish an HRV range at all, because overnight RMSSD spans an order of magnitude between
healthy adults. **An absolute HRV alarm is therefore not offered as a suggestion**, and the builder
lets a caregiver express one only as a share of that member's own usual.

**The 70% figure is ours, and is a starting point rather than a finding.** No body publishes an HRV
deviation threshold — that is the whole point of the row above. The nearest external anchors are
Oura's 3–5 bpm resting-heart-rate deviation and third-party guidance around 10–15% from a 30-day
rolling average. Treat 70% as a number to calibrate against our own cohort, and say so wherever it
appears.

### 3.4 Personal baselines generally

CardiTrack's baseline-relative threshold kinds (`BaselinePercent`, `BaselineSigma`) resolve against
the **established 30-day** `PatternBaseline` only. A member without one gets
`AlarmEvaluationState.InsufficientData` and no alert — the same provisional-never-alerts rule the
nine built-in rules follow, reached the same way: by what the engine fetches.

This is the direct analogue of CloudWatch's `ANOMALY_DETECTION_BAND`, which builds a confidence band
from a metric's own history rather than a fixed line. Naming that parallel is useful when explaining
the design to engineers; it is not a claim about clinical validity.

### 3.5 Not implemented — recorded so the reasoning is not lost

**Blood pressure.** The 2025 AHA/ACC guideline keeps the 2017 categories: normal <120/<80; elevated
120–129/<80; stage 1 130–139 **or** 80–89; stage 2 ≥140 **or** ≥90; and ≥180/120 as an emergency
alongside symptoms. Two implementation notes for whoever builds this: the category rule is **OR**
across systolic and diastolic, not AND — an alarm implemented as AND would be a different rule from
the guideline it cites; and the AHA now prefers "markedly elevated BP" to "hypertensive urgency"
where there is no organ damage, so neither term should be hard-coded into an enum or into copy.
Sources: https://www.ahajournals.org/doi/10.1161/HYP.0000000000000249 ·
https://professional.heart.org/en/science-news/2025-high-blood-pressure-guideline/top-things-to-know ·
https://www.heart.org/en/health-topics/high-blood-pressure/understanding-blood-pressure-readings/when-to-call-911-for-high-blood-pressure

All guideline thresholds assume a validated, correctly sized cuff and correct technique. Wrist-cuff
and cuffless optical estimates do not meet that assumption, so an alarm built on them would not be
measuring what the guideline defines.

**Weight.** The AHA's patient education gives 2–3 lb in a day or more than 5 lb in a week as a sign
of worsening heart failure; the AAHFN's daily-weights guidance says 2 lb in a day or 5 in a week.
Sources disagree on the daily figure, so 3 lb would be the noise-reducing choice.
Sources: https://www.heart.org/en/health-topics/heart-failure/warning-signs-of-heart-failure/managing-heart-failure-symptoms ·
https://www.aahfn.org/mpage/dailyweights

**The caveat matters more than the number.** The 2022 AHA/ACC/HFSA heart-failure guideline does
**not** recommend telemonitoring of vital signs and weight as a strategy to reduce heart-failure
hospitalisation. A weight alarm would therefore ship as an adherence and education aid — "your care
team may want to know" — never as detection. Source:
https://www.ahajournals.org/doi/10.1161/CIR.0000000000001063

---

## 4. Why the controls, not the thresholds, are the design

A threshold is easy. Not destroying the caregiver's trust in the alarm is the hard part, and it is
well documented.

| Source | Finding | URL |
|---|---|---|
| The Joint Commission, Sentinel Event Alert 50 | 80 alarm-related deaths reported Jan 2009 – Jun 2012; led to National Patient Safety Goal NPSG.06.01.01 on clinical alarm safety | https://digitalassets.jointcommission.org/api/public/content/f65e5c9df2b94000a99445e0a7877007 |
| Joint Commission Journal | Consensus that physiological monitors produce false alarms at rates of **86–99.5%** | https://www.jointcommissionjournal.com/article/S1553-7250(16)42039-8/abstract |
| ECRI | Alarm hazards have appeared on the top-ten health technology hazards list since 2011, ranked #1 in 2012; the 2020 framing is "alarm, alert, and notification **overload**" — a cognitive-load problem, not a device one | https://smart-healthcare-safety.ecri.org/e/combatting-alarm-fatigue/ |
| AACN Practice Alert | Alarms per ICU patient rose from ~6 to ~40 since 1983, while people struggle to learn more than about **six** distinct alarms; recommends unit-specific default parameters | https://www.aacn.org/newsroom/practice-alert-outlines-alarm-management-strategies |
| Consumer-cardiac study | Receiving **false** AF alerts was associated with a dose-dependent decline in self-perceived physical health and in disease self-management | https://www.ncbi.nlm.nih.gov/pmc/articles/PMC10358285/ |
| Apple Heart Study, NEJM 2019 | 0.5% of participants received an irregular-rhythm notification overall — but 3.2% of those 65+ against 0.16% of those 22–39 | https://www.nejm.org/doi/full/10.1056/NEJMoa1901183 |

The last row is why CardiTrack's population matters: the base rate of *anything* firing is far
higher in the people this product watches than in the general population, so the same alarm design
produces far more notifications here.

What this buys, in code:

| Control | Where | What it prevents |
|---|---|---|
| **M of N datapoints** | `MetricAlarmEvaluator` | One odd measurement waking a family. Breaches need not be consecutive |
| **Hysteresis** (clear 5% inside the firing threshold) | `MetricAlarmEvaluator.HysteresisFraction` | A value sitting on the line paging on every crossing all afternoon. The 5% is engineering judgement; the need for it is not |
| **Transition-only firing** | `MetricAlarmState` | A five-minute cron re-raising the same standing condition twelve times an hour |
| **Threshold bounds per metric** | `AlarmMetricCatalogue` | The bradycardia trap in §3.1. Apple and Fitbit bound theirs for the same reason |
| **Stillness gate** | `AlarmContextGate.Inactive` | Stair climbs reading as tachycardia |
| **Ceiling of 12 enabled alarms, advisory past 6** | `MetricAlarmValidation` | The AACN finding above. Past a handful a caregiver cannot account for what they switched on, and alarms nobody can account for get silenced wholesale |
| **Red requires explicit confirmation** | `SaveMetricAlarmRequest.ConfirmCriticalSeverity` | A mis-tapped severity waking a family at 3am |
| **No `breaching` missing-data option** | `AlarmMissingDataTreatment` | A watch on a bedside table reading as a crisis — see §6 |

Quiet hours, the escalation ladder and per-category muting are unchanged: a custom alarm goes
through the same `DeliveryPlanner` policy as everything else, so a Yellow still does not push.

---

## 5. How the numbers must be presented

**Presenting a threshold as the caregiver's own preference is both the honest framing and the
protective one.** Two FDA guidances, both finalised 6 January 2026, set the line:

| Guidance | What it says | URL |
|---|---|---|
| General Wellness: Policy for Low Risk Devices | Non-invasive products may estimate or output physiologic parameters — blood pressure, oxygen saturation, HRV, heart rate — for general wellness, **provided they do not reference specific diseases or diagnostic thresholds** | https://www.fda.gov/regulatory-information/search-fda-guidance-documents/general-wellness-policy-low-risk-devices |
| Clinical Decision Support Software | Adds emphasis on transparency about data inputs, underlying logic, and how a recommendation is generated, while avoiding information overload | https://www.fda.gov/regulatory-information/search-fda-guidance-documents/clinical-decision-support-software |

The July 2025 WHOOP warning letter is the on-point precedent: FDA alleged that "Blood Pressure
Insights" was marketed without clearance, on the strength of the claims and presentation rather than
the computation underneath. Analysis:
https://www.troutman.com/insights/fdas-2026-guidance-on-general-wellness-devices-policy-for-low-risk-devices/

Concrete rules, which `MetricAlarmNarrative` implements:

1. **Never name a condition.** "Their resting heart rate was 104 bpm, above the 100 bpm you asked to
   be told about" — never "tachycardia", never "signs of hypertension".
2. **Never present a threshold as diagnostic.** It is the level *this caregiver set*. The guideline
   behind it belongs on a clearly-labelled reference screen, not in the alert.
3. **Direct to a clinician; never advise treatment.** No dosing, no "you can wait", no reassurance.
4. **Surface the reasoning.** Every alert carries its metric, threshold, window, the observed value
   and how many datapoints breached, in `Alert.MetricValues`. That is the CDS transparency the 2026
   guidance asks for, and it is also what makes an alarm contestable.
5. **Put the measurement caveat next to the alarm.** Specifically the FDA pulse-oximetry accuracy
   and skin-tone caveat on any SpO2 alarm.

Counsel should review this catalogue and its copy before public launch. The wellness carve-out is
real but narrow, and the WHOOP letter shows where the line is drawn.

---

## 6. Cloud alarm semantics — what we borrowed and what we did not

The grammar is AWS CloudWatch's and GCP Cloud Monitoring's, deliberately, because it is the
vocabulary that already exists for this problem.

| Concept | Cloud origin | CardiTrack |
|---|---|---|
| OK / ALARM / INSUFFICIENT_DATA | CloudWatch alarm states | `AlarmEvaluationState`, same three, same names |
| M of N datapoints | CloudWatch "datapoints to alarm" over "evaluation periods"; breaches need not be consecutive | `DatapointsToAlarm` / `EvaluationPeriods`, same semantics |
| Treat missing data | CloudWatch `missing` (default) / `notBreaching` / `ignore` / `breaching` | `AlarmMissingDataTreatment` — **three of the four** |
| Metric absence as its own policy | GCP makes metric-absence a separate condition type rather than a flag | `InactivityDetectionWorker`'s device-silence alert, which already existed |
| Severity tiers | GCP alerting policies carry exactly three: CRITICAL / ERROR / WARNING | The product's existing Yellow / Orange / Red |
| Duration before firing | GCP's retest window; guidance is to set it to more than double the sampling rate | `PeriodMinutes` floored at 5 against a 10-minute ingestion poll |
| Anomaly band | CloudWatch `ANOMALY_DETECTION_BAND` | `AlarmThresholdKind.BaselineSigma` against the member's own 30-day baseline |
| Composite alarms, suppressor alarms | CloudWatch | **Not built.** Real features, genuine over-engineering for a family product; the stillness gate covers the one suppression case that matters |

Docs: https://docs.aws.amazon.com/AmazonCloudWatch/latest/monitoring/alarms-and-missing-data.html ·
https://docs.aws.amazon.com/AmazonCloudWatch/latest/monitoring/alarm-evaluation.html ·
https://docs.cloud.google.com/monitoring/alerts/concepts-indepth

**Why `breaching` is not offered.** Treating an absent datapoint as over the line converts "the
watch is off the wrist" into a page at three in the morning. It also contradicts the null-vs-zero
discipline this product holds everywhere else — a missing reading means *not measured*, never *did
nothing* — which the algorithm card names as a gate on every statistical rule. Data absence keeps
its own producer, which is the separation GCP draws by making metric-absence its own policy type.

**Severity as required response latency, not magnitude.** The Google SRE workbook's page/ticket
split turns on how fast a human must respond, not on how big the number is
(https://sre.google/workbook/alerting-on-slos/). Red is "act now"; Orange is "look today"; Yellow is
"read it when you read the app". The suggested set in §2 is assigned on that basis.

---

## 7. Where sources disagree, or where a number is ours

Every item here should be treated as an open question rather than a settled default.

1. **Bradycardia floor: 60 (clinical) vs 40–50 (product).** The largest divergence in this document.
   Discussed in §3.1. Ours is 40, following the vendors.
2. **HRV deviation: 70% is ours.** No authority publishes one. §3.3.
3. **SpO2 ladder: 95 / 92 / 90 / 88 is a synthesis**, not a single citation. Cleveland Clinic gives
   several tiers; other bodies frame the question differently, and some guidance targets acute
   inpatient care rather than a person at home.
4. **Daily weight gain: 2 lb (AAHFN) vs 2–3 lb (AHA).** Moot until weight is ingested. §3.5.
5. **Weight telemonitoring efficacy is not established** — the 2022 heart-failure guideline is
   explicitly unconvinced. §3.5.
6. **Apple's 10-minute inactivity window is a product default** with no published clinical
   derivation. We inherit the shape for familiarity, not for evidence.
7. **Hysteresis at 5% is engineering judgement.** The need for hysteresis is citable; the figure is
   not.
8. **AF notification performance varies enormously by population** — reported sensitivity ranges
   from 41% to 72% across cohorts. Any accuracy claim must name its population. Moot until a rhythm
   flag is ingested.

---

## 8. Change control

A change to a suggested default, a threshold bound, or the evaluator's arithmetic is a **DPIA §13
review trigger** and an **Art. 22 V4 event**, on the same footing as a change to a built-in rule's
constant — see [alerting_algorithm_card.md §6](../compliance/alerting_algorithm_card.md) and
[art22_alerting_analysis.md §5](../compliance/art22_alerting_analysis.md). Record it there before
mixing pre- and post-change rows in any comparison.

---

**Related:** [alerting_algorithm_card.md](../compliance/alerting_algorithm_card.md) ·
[art22_alerting_analysis.md](../compliance/art22_alerting_analysis.md) ·
[alerts.md](../execution/backend/api/alerts.md) ·
[entity_summary.md](./entity_summary.md)

**Last Updated:** September 6, 2026
