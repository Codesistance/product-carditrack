# Fitbit-identifiable patterns — cardiovascular and neurological

**Status: product/clinical reference.** Companion to
[alarm_catalogue.md](alarm_catalogue.md) (caregiver-set thresholds) and
[alerting_algorithm_card.md](../compliance/alerting_algorithm_card.md) (the nine
rules CardiTrack already runs). This document answers a different question:
which **established** early-warning patterns of serious cardiovascular or
neurological trouble a Fitbit wrist wearable can actually see, and which of
those CardiTrack currently stores.

> ### How to read this
>
> **This is not a diagnostic catalogue and it is not copy.** CardiTrack is a
> general-wellness family monitor, not a medical device — see
> [solution_manifest.md § Regulatory posture](../solution_manifest.md).
> Family-facing alerts must never name a condition. The condition names below
> exist so engineering and product can tell a viable signal from a wish; they
> do not belong in an alert, a digest, or marketing.
>
> A pattern is listed only when (a) published work or a named guideline
> associates that *shape in wearable data* with a serious outcome, and (b) a
> Fitbit (or Pixel Watch, which shares the same Google Health API types) can
> produce the underlying readings. Association is not prediction. Several
> rows are population-level risk, not an acute page.
>
> **Nothing here is medical, legal or regulatory advice.** Before a sentence
> is quoted externally, open the cited URL.

---

## 1. Three layers, because they are not the same list

| Layer | What it answers | Authority |
|---|---|---|
| **Hardware** | Does a Fitbit wrist device *measure* this? | [Google Health API data types](https://developers.google.com/health/data-types) compatible-device lists; Google ECG / Irregular Rhythm help |
| **API** | Does Google Health API v4 *expose* it for a third party? | Same data-types table, plus OAuth scope |
| **CardiTrack** | Do we *store* it, and do we already *rule* on it? | `GoogleHealthApiClient`, `ActivityLog`, `GranularMetric`, `StatisticalAlertRules`, `AlarmMetricCatalogue` |

Fitbit-the-hardware is richer than Fitbit-through-CardiTrack. The OAuth grant
CardiTrack requests today is four read scopes
(`activity_and_fitness`, `health_metrics_and_measurements`, `sleep`,
`settings`) — see `CardiTrack.API/appsettings.json`. It does **not** request
`ecg.readonly` or `irn.readonly`, so on-device AFib signals never arrive. It
also never persists `weight`, even though the same health-metrics scope would
serve an Aria scale. Those gaps are called out per row rather than papered
over.

**Device mix.** Charge 6 is the product's advertised Fitbit. Older Alta /
Inspire SKUs miss SpO2, skin temperature and (on some) HRV. ECG exists only
on Charge 5/6 and Sense/Sense 2. A pattern that needs a missing sensor is
not "Fitbit-identifiable" for that wearer.

---

## 2. What CardiTrack actually pulls from a Fitbit today

From [data_sync_architecture.md](data_sync_architecture.md): 18 daily Health
API calls plus 5 granular series per day-in-window.

| Family | Google Health types we read | Stored as |
|---|---|---|
| Activity | `steps`, `distance`, `active-minutes`, `floors`, `total-calories`, `sedentary-period`, `activity-level`, `time-in-heart-rate-zone`, `daily-heart-rate-zones`, `daily-vo2-max` | `ActivityLog` daily columns; minute `steps` and `active-zone-minutes` in `GranularMetricHours` |
| Heart | `heart-rate`, `daily-resting-heart-rate`, `daily-heart-rate-variability`, `heart-rate-variability` | Daily RHR / RMSSD; minute HR and sparse HRV |
| Oxygen / breathing | `oxygen-saturation`, `respiratory-rate-sleep-summary`, `daily-respiratory-rate` | Daily SpO2 min/avg/max, overnight and daily breathing rate; ~5-min SpO2 samples |
| Sleep | `sleep` (session + stages) | Duration, start/end, efficiency, deep / light / REM / awake minutes |
| Temperature | `daily-sleep-temperature-derivations` | Nightly wrist temperature vs the device's own baseline |
| Not pulled | `electrocardiogram`, `irregular-rhythm-notification`, `weight`, `blood-glucose`, `core-body-temperature`, `exercise` GPS | — |

The nine built-in rules and the R2 alarm builder can only watch this set.
`AlertRuleCatalogue` already *reserves* ids for `fragmented_sleep`,
`overnight_vitals`, `multi_signal_cluster` and `baseline_shift`; those
producers are not shipped.

---

## 3. Cardiovascular patterns Fitbit data can identify

Each row is a **pattern in time-series**, not a diagnosis. "Viability" here
means: the association is published, and the inputs exist on a current Fitbit
wrist device.

### 3.1 Identifiable from data we already ingest

| Pattern (what the literature describes) | Fitbit inputs | What the evidence actually says | CardiTrack today |
|---|---|---|---|
| **Resting heart rate rising off the person's own baseline** | `daily-resting-heart-rate` | In a 249-person Fitbit HF study, RHR rose in the **three days** before a hospital visit (`p = .022`); a population split on baseline RHR did **not** separate the groups — the signal is the *change*, not the absolute bpm. A separate wearable HF series saw RHR up ~11 bpm two weeks before decompensation. A >5 bpm nocturnal RHR rise on a wearable defibrillator more than doubled later CV hospitalisation/mortality. | **Ruled.** `elevated_heart_rate`: yesterday's RHR > 30-day mean + max(2σ, 5 bpm). Suggested custom alarm: RHR > baseline + 2σ. |
| **Heart-rate variability falling off the person's own baseline** | overnight RMSSD (`daily-heart-rate-variability`) | Implantable and wearable series show HRV declining before HF decompensation as sympathetic tone rises; low HRV also predicts incident HFpEF in the Women's Health Initiative. No published *absolute* RMSSD band exists — CardiTrack already refuses to invent one (`HealthReferenceRanges.NoHeartRateVariabilityBand`). | **Ruled.** `hrv_drop`: overnight RMSSD below mean − max(2σ, 15% of mean) on **both** of the last two nights. Suggested custom alarm: overnight HRV < 70% of baseline (our starting point, not a finding). |
| **Overnight breathing rate rising** | `respiratory-rate-sleep-summary` | Nightly respiratory rate is elevated before decompensation on ICD/CRT populations. On a consumer wearable the same physiology is plausible; one exploratory multiparameter study still found RR data quality poor and the model insensitive, so this is a **supporting** signal, not a lone detector. Fitbit Charge 4 vs reference: RR agreement was good (ICC ~0.8) but **small day-to-day changes were missed**. | **Ruled.** `overnight_breathing_up`: last night's asleep rate > mean + max(2σ, 1 breath/min). |
| **Sustained activity / step decline** | daily `steps`; minute steps | The same Fitbit HF cohort: people who went on to a hospital visit took fewer steps (`p = .002`), and a low baseline step count itself marked higher visit probability. Activity falling while vitals rise is the decompensation cluster, not a sports-recovery dip. | **Ruled.** `activity_decline` (yesterday < 70% of 30-day mean) and `long_term_trend` (four weeks each ≥5% below the previous week). |
| **Heart rate up without matching movement** | granular `heart-rate` + `steps` / `time-in-heart-rate-zone` + activity-decline | Raised-zone minutes after a walk are exercise. The same minutes on a day the person barely moved are the wearable analogue of "tachycardia at rest" — illness, arrhythmia, or pain. Apple and Fitbit themselves gate high-HR notifications on ~10 minutes of inactivity for this reason. | **Ruled.** `elevated_zone_without_movement` (zone minutes above a floor **and** the activity-decline rule). Real-time SSA path watches the latest hour of HR. Custom high-HR alarm is stillness-gated. |
| **Blood oxygen trending down** | `oxygen-saturation` samples, daily min/avg | WHO: 94–100% normal at sea level; <90% severe hypoxaemia. Cleveland Clinic: contact a provider ≤92%, seek help ≤88%. **FDA:** a cleared oximeter reading 90% may be a true 86–94%, and decisions should follow **trends**, not a single point; consumer Fitbit SpO2 is not a cleared oximeter. Charge 4 vs reference in COPD: SpO2 agreement was **poor** (ICC 0.32) and overestimated saturation. | **Ingested, not a built-in rule.** Suggested custom alarms: avg SpO2 <90% over 5 min (3 of 3, Red) and <92% over 10 min (2 of 3, Orange). Dashboard shows the reading against the WHO band; there is no SpO2 baseline yet. |
| **Sleep duration / timing breaking the person's usual** | `sleep` sessions | Short or fragmented sleep is a cardiovascular risk marker in population studies and a non-specific prodrome (pain, infection, HF orthopnoea, mood). It does not identify a cardiac event on its own. | **Ruled.** `irregular_sleep` (±30% of usual minutes). A longer night that is still inside the NSF band is not alerted. |
| **Long unbroken daytime stillness** | `activity-level` intervals with the night clipped out | A still afternoon in an older adult is how a faint, a fall that left them on the floor, or an acute illness presents *before* anyone calls. It is not specific to heart vs brain vs "they sat in a chair". | **Ruled.** `daytime_inactivity_block` (settings title: "Long daytime rest"): one sedentary stretch > max(3 h, usual longest + 50%). |
| **No movement after typical wake, with a measured zero** | minute `steps`, circular-mean wake time | A **measured** zero (the watch is on and reporting nothing moved) after the person's usual morning is the closest Fitbit-shaped proxy for an unwitnessed collapse — cardiac, neurological, or a fall. A missing reading is the opposite case and must not page as this. | **Ruled.** `no_morning_activity`: measured 0 steps, local time ≥ typical wake + 2 h. The rule emits a finding; MedGemma sets the severity the family sees (the constant this rule carried until 2026-09-19 was Red). Device silence is a separate Yellow Worker rule (`InactivityDetectionWorker`) and is not model-judged. |
| **Intraday heart-rate shape leaving its own trend** | minute `heart-rate` | SSA over a 60-minute window (≥45 covered minutes) separates trend / oscillation / noise. A jump (≥3 typical jitters from trend) is a *shape* change, not a bpm threshold — the wearable equivalent of "this hour does not look like the hours around it". | **Ruled (dev assessor).** MedGemma interprets; only red/orange verdicts become `HeartRate` alerts. |

### 3.2 Identifiable on Fitbit hardware, not ingested by CardiTrack

These are the highest-leverage Fitbit cardiac signals we do **not** currently
see. Adding any of them is an ingestion + consent + DPIA change, not an
alarm-threshold change.

| Pattern | Fitbit inputs | Evidence | Why we do not see it |
|---|---|---|---|
| **Irregular pulse suggestive of atrial fibrillation** (passive) | `irregular-rhythm-notification` — PPG while still or asleep | FDA-cleared (K212372, product code QDB). Validation study NCT04380415: 455,699 subjects; of 1,057 who returned a usable 7-day ECG patch after a notification, **32.2%** had AFib on the patch; when AFib was present *during* simultaneous wear, agreement was 98.2% (221/225). It is opportunistic, not every-episode, not for people already diagnosed with AFib, and not for under-22s. Compatible devices include Charge 3–6, Sense/Sense 2, Versa 2–4, Inspire 2/3, Luxe, Fitbit Air, and Pixel Watch. | Scope `googlehealth.irn.readonly` is not requested. `alarm_catalogue.md` already names this as a missing metric. |
| **On-demand single-lead ECG classified as AFib vs sinus** | `electrocardiogram` session (`SINUS_RHYTHM` / `ATRIAL_FIBRILLATION` / `INCONCLUSIVE`) | Google ECG app on Charge 5/6, Sense/Sense 2, Pixel Watch. Spot-check only; the wearer has to start it. Qualitatively Lead-I-like for AFib vs sinus — **not** ST-elevation, ischaemia, or ventricular arrhythmia detection. | Scope `googlehealth.ecg.readonly` is not requested. |
| **Rapid weight gain** (heart-failure fluid) | `weight` from **Aria / Aria 2 / Aria Air**, not from a wrist Fitbit | AHA patient education: 2–3 lb in a day or >5 lb in a week; AAHFN: 2 lb / 5 lb. The 2022 AHA/ACC/HFSA guideline does **not** recommend vital-sign-and-weight telemonitoring to cut HF hospitalisation, so this would be an adherence aid, not detection. | No weight column. The same health-metrics scope would serve a scale; we simply never call `weight`. Wrist Fitbits do not produce this type. |

AFib is the one Fitbit signal with a **direct, named** path to a serious
neurological outcome (ischaemic stroke). Until IRN/ECG are ingested, CardiTrack
can only infer "something cardiac-shaped is off" from rate, HRV and motion —
it cannot see the irregular-rhythm flag the device already computed.

### 3.3 Cardiovascular patterns Fitbit wrist data cannot identify

| Pattern | Why not |
|---|---|
| **Hypertensive emergency / the 2025 AHA/ACC BP categories** | No systolic or diastolic type from a Fitbit wrist device. Cuffless optical BP is not what those guidelines measure. |
| **Acute coronary syndrome / STEMI** | Fitbit ECG, where it exists, classifies AFib vs sinus. It does not do 12-lead ischaemia analysis. |
| **Ventricular tachycardia / VF as a typed event** | No rhythm flag beyond IRN's AFib-oriented PPG algorithm. Extreme rate + stillness is the only proxy, and it is non-specific. |
| **Pulmonary congestion / thoracic fluid** | The wearables that reduced HF hospitalisation in the 2025 meta-analysis were purpose-built congestion sensors (e.g. Zoll HFMS), not a Fitbit. |
| **Valvular disease, EF, murmur** | No imaging, no auscultation, no impedance. |
| **Core temperature / fever as a CV precipitant** | Fitbit exposes *wrist sleep-temperature derivations*, not core body temperature (`core-body-temperature` is a different Health API type Fitbit wrists do not fill). |

---

## 4. Neurological patterns Fitbit data can identify

Wrist PPG and actigraphy do not see the brain. What they see is **behaviour
and autonomic tone** that published cohorts have associated with stroke risk,
Parkinson's, and dementia. These are slower, less specific, and easier to
over-claim than the cardiac rows. The viable ones are listed; the rest are
in §4.3 so they are not rediscovered as "gaps to hack around".

### 4.1 Identifiable from data we already ingest

| Pattern (what the literature describes) | Fitbit inputs | What the evidence actually says | CardiTrack today |
|---|---|---|---|
| **Sudden stop in movement after the person would normally be up** | minute `steps`, sleep-derived wake time | Unwitnessed stroke, TIA, syncope and a fall look the same from the wrist: the person does not get going. Specificity is low; that is why the rule demands a *measured* zero rather than missing data, and a two-hour grace past usual wake. | **Ruled.** Same `no_morning_activity` / `daytime_inactivity_block` / device-silence trio as in §3.1. |
| **Progressive loss of daily steps (and of step intensity)** | daily `steps`; minute cadence can be derived from granular steps | UK Biobank, 78,430 adults, wrist accelerometer (Axivity AX3, not a Fitbit — same *class* of signal): ~3,800 steps/day associated with 25% lower incident-dementia hazard, ~9,800 with ~51% lower (HR 0.49); purposeful steps (≥40/min) and peak-30-minute cadence were stronger than a raw count. Observational, not a claim that walking prevents dementia. In Parkinson's, daily steps run lower than peers and track severity. | **Partially ruled.** `activity_decline` and `long_term_trend` watch volume, not cadence. Peak-30-minute cadence is computable from granular steps and is **not** a shipped feature. |
| **Circadian rest–activity rhythm breaking up** | `activity-level` + `steps` across 24 h; sleep timing | In early Parkinson's, less *stable* day-to-day rest–activity rhythm predicted poorer executive, visuospatial and psychomotor scores **independently of sleep**. PD cohorts show lower rhythm amplitude (less day activity, more night activity) and more fragmentation. This is a trajectory marker, not an acute alert. | **Not ruled.** The inputs exist (`activity-level` is already pulled to measure the longest still stretch; sleep start/end are stored). Interdaily stability / relative amplitude are not computed. Catalogue id `baseline_shift` is reserved. |
| **Sleep fragmentation (WASO, efficiency, night movement)** | `sleep` stages: awake minutes, efficiency, duration | Actigraphy sleep-fragmentation index in PD associated with worse MoCA and delayed recall; total sleep time and self-reported sleepiness did not. Consumer sleep staging is not polysomnography — Fitbit will overestimate sleep in a still Parkinsonian night — so treat fragmentation as a *direction*, not a stage diagnosis. | **Ingested, not ruled.** `AwakeMinutes` / `SleepEfficiency` are on `ActivityLog`. Catalogue id `fragmented_sleep` is reserved, producer unbuilt. `irregular_sleep` only watches total minutes. |
| **Overnight autonomic shift (HRV down, sleeping HR up)** | overnight RMSSD, sleeping HR (from granular HR clipped to the sleep session) | Autonomic failure is a Parkinson's and synucleinopathy feature; the same HRV drop is also an infection/HF signal (§3.1). It cannot tell heart from brain. It *can* tell "the night's autonomic picture moved". | **Partially ruled.** `hrv_drop` is the HRV half. Sleeping-HR vs usual is not a named rule (the real-time SSA hour can catch a wild night). |
| **Atrial fibrillation as stroke risk** | see §3.2 | AFib is the wearable's one **typed** stroke-risk finding. CHA₂DS₂-VASc is clinical, not a Fitbit output; the device only flags the rhythm. | **Not ingested** — listed here because the neurological stake is the reason to care about IRN, not because we have the flag. |

### 4.2 Weak but real: supporting signals, not detectors

| Pattern | Fitbit inputs | Why it stays supporting |
|---|---|---|
| **REM-sleep share drifting down** | `sleep` REM minutes | REM-sleep behaviour disorder is a Parkinson's/DLB prodrome — on **PSG with EMG**, not on a wrist optical sleep stage. Fitbit REM is an estimate. Directional only. |
| **Wrist temperature off the person's nightly baseline** | `daily-sleep-temperature-derivations` | Useful as an infection/circadian cue. Family digests already **omit** skin temperature (too intimate, easy to over-read) — [llm_design.md](../llm_design.md). Not a stroke or dementia marker. |
| **VO2 max drifting down** | `daily-vo2-max` | Long-horizon fitness, not an acute neurological event. Ingested, unused by any rule. |

### 4.3 Neurological patterns Fitbit wrist data cannot identify

| Pattern | Why not |
|---|---|
| **FAST symptoms, localisation, haemorrhage vs infarct** | No camera, no speech, no NIHSS. Sudden stillness is the only wrist proxy and is shared with cardiac collapse and a nap. |
| **Seizure** | No EEG. Motion spikes are confounded by tremor, cars, and toothbrushing. |
| **Parkinsonian tremor / bradykinesia kinematics** | Fitbit does not expose tremor amplitude, gyro features, or a UPDRS-like motor score. Research watches that do (e.g. Verily Study Watch in PPMI) are a different device. |
| **Gait speed, stride variability, dual-task cost** | Steps and cadence only. No insole, no 6-minute-walk test. |
| **Cognitive score, aphasia, neglect** | No task battery. |
| **Wandering / geofence** | `googlehealth.location.readonly` is not requested; the enricher that would read exercise GPS is unprovisioned and never stores a coordinate. |
| **Fall as a typed event** | No `fall` data type on the Google Health API. Apple-style fall detection is not a Fitbit Health API series. Stillness after a crash is the proxy in §4.1. |
| **Orthostatic hypotension** | Needs paired BP (lying/standing) or a beat-to-beat BP sensor Fitbit does not have. |

---

## 5. The combinations that actually carry viability

Paging a family on one metric in isolation is how alarm fatigue starts: the
false ones arrive first, and the real cluster is then ignored. The published
HF and neurodegenerative work is almost all **clusters over days**, against
the person's own baseline. These are the Fitbit-feasible clusters, strongest
first.

| Cluster | Inputs we already have | Typical horizon | Serious outcomes the literature ties it to | Product status |
|---|---|---|---|---|
| **1. Quiet days + rising RHR + falling HRV + rising overnight RR** | steps, RHR, overnight RMSSD, sleep RR | 3–14 days | HF decompensation / unplanned cardiac hospital visit | Four separate rules can each fire. **`multi_signal_cluster` is reserved and unbuilt** — today a family may get four cards instead of one composed finding. |
| **2. Stillness at rest with a high heart rate** | granular HR + zero steps (or zone minutes on an activity-decline day) | minutes–hours | Resting tachycardia, possible arrhythmia, febrile illness | Shipped (`elevated_zone_without_movement` + stillness-gated custom HR alarms + SSA hour). |
| **3. Measured zero movement past usual wake** | steps + circular-mean wake | same morning | Unwitnessed collapse (cardiac, neurological, fall) | Shipped (`no_morning_activity`). Finding only; severity is MedGemma's verdict (former constant: Red). |
| **4. Weeks of falling steps, flattening cadence, less-stable 24 h rhythm, more fragmented sleep** | steps, granular cadence, `activity-level`, sleep stages | weeks–months | Frailty, Parkinson's progression, dementia *risk* (population), depression | Volume trend shipped (`long_term_trend`). Cadence, interdaily stability and fragmentation are **not** computed. |
| **5. Repeated irregular-rhythm notifications / AFib ECG class** | IRN + ECG | opportunistic | AFib → ischaemic stroke risk | **Hardware yes, CardiTrack no.** Highest-value ingestion gap on this list. |
| **6. SpO2 trending down across nights, especially with rising overnight RR** | SpO2 samples, sleep RR | nights | Hypoxaemia, sleep-disordered breathing, HF, COPD exacerbation | Inputs stored. No built-in pairing. Custom SpO2 alarms exist as suggestions. Treat absolute % with the FDA error band. |

Cluster 1 is the closest Fitbit gets to "this person is heading toward a
serious cardiac admission." Cluster 3 is the closest it gets to "check on
them right now." Cluster 4 is the closest it gets to a neurological
trajectory — and it must never be narrated as a dementia or Parkinson's
finding.

---

## 6. What this means for the product (four risks, short)

| Risk | Assessment | Severity | Evidence needed to de-risk |
|---|---|---|---|
| Value | Caregivers already buy "catch it before 999." The Fitbit-viable set is real, and most of the high-value cardiac rows are **already ruled**. The gap that would change a Complete Care conversation is IRN/ECG (typed AFib) and a composed multi-signal card rather than four overlapping alerts. | 🟢 cardiac / 🟠 neuro | Would families pay more for an AFib flag they can already see in the Fitbit app? Prototype the composed cluster, not a new metric. |
| Usability | Naming HF, AFib, stroke or Parkinson's in UI is both illegal-feeling and unusable at 2am. The existing "never name a condition" rule in `MetricAlarmNarrative` / digest prompts is the usability design, not a cop-out. | 🟢 if we keep the register | Copy review of any new cluster card. |
| Feasibility | No new device integration is required for §3.1 / §4.1. IRN/ECG are new Restricted scopes (`irn.readonly`, `ecg.readonly`) on the existing Google Health client — verification, DPIA, and a 100-wearer cap interaction. Cadence / circadian features are .NET arithmetic over data we already store. | 🟢 for derived features; 🟠 for IRN/ECG | Scope-verification timeline; whether Fitbit IRN sessions actually appear for our test wearers. |
| Viability | FDA General Wellness (final 6 Jan 2026): physiologic parameters are in-bounds **provided the product does not reference specific diseases or diagnostic thresholds**. The WHOOP BP letter is the warning. AFib IRN is itself a cleared medical-device output; *relaying* it without becoming a device is a counsel question, not a prompt tweak. | 🔴 for any condition-named surface; 🟠 for relaying IRN | Counsel review before IRN/ECG ingestion or any cluster named after a disease. |

**Verdict: keep using the ingested patterns as wellness deviations; do not
build a "stroke predictor" or "HF detector."** The next product move with
evidence behind it is (1) a composed multi-signal finding from rules we
already run, and (2) a scoped decision on IRN/ECG — not new wrist maths
pretending to see the brain.

**Out of scope for this document:** implementing IRN/ECG, cadence features,
or `multi_signal_cluster`. Those are separate specs. This catalogue exists
so those specs start from what Fitbit can actually see.

---

## 7. Sources

| # | Claim it backs | URL |
|---|---|---|
| S1 | Google Health API types, scopes, Fitbit-compatible devices (ECG, IRN, SpO2, RR, HRV, weight/Aria) | https://developers.google.com/health/data-types |
| S2 | ECG and IRN record shapes; AFib classification enum | https://developers.google.cn/health/data-types/vitals |
| S3 | Fitbit IRN 510(k) indications (K212372) — opportunistic AFib, still-only, not for known AFib, ≥22 | https://www.accessdata.fda.gov/cdrh_docs/pdf21/K212372.pdf |
| S4 | IRN validation NCT04380415 — 32.2% patch-positive after notification; 98.2% agreement when AFib was simultaneous | https://support.google.com/googlehealth/answer/14236719 |
| S5 | Fitbit ECG app — AFib vs sinus vs inconclusive; Charge 5/6, Sense/Sense 2, Pixel Watch | https://support.google.com/fitbit/answer/14236718 |
| S6 | Fitbit HF cohort: fewer steps, RHR up 3 days pre-visit | https://doi.org/10.64898/2026.03.26.26349411 |
| S7 | Multiparameter wearable HF: RHR +11 bpm and IBI/HRV down 2 weeks pre-decompensation | https://pmc.ncbi.nlm.nih.gov/articles/PMC12507380/ |
| S8 | Wearable HF decompensation review (RHR, HRV, activity, respiratory pattern) | https://doi.org/10.3390/jcm14207423 |
| S9 | Congestion-sensing wearables meta-analysis — **not** Fitbit; listed so we do not over-claim | https://www.frontiersin.org/articles/10.3389/fcvm.2025.1612545 |
| S10 | Fitbit Charge 4 vs reference in COPD: steps/RHR/RR good, SpO2 poor (ICC 0.32) | https://mhealth.jmir.org/2024/1/e56027 |
| S11 | UK Biobank steps vs incident dementia (wrist accelerometer, n=78,430) | https://doi.org/10.1001/jamaneurol.2022.2672 |
| S12 | PD rest–activity rhythm stability vs cognition, independent of sleep | https://pmc.ncbi.nlm.nih.gov/articles/PMC6277371/ |
| S13 | Smartwatch activity/sleep vs PD non-motor features (PPMI / Verily) | https://doi.org/10.1038/s41531-024-00719-w |
| S14 | PD actigraphy sleep fragmentation vs MoCA | https://doi.org/10.1002/alz.061544 |
| S15 | Quer et al. — wearable RHR + sleep vs infection (personal baseline) | https://www.nature.com/articles/s41591-020-1123-x |
| S16 | FDA pulse-oximeter accuracy / skin tone | https://www.fda.gov/medical-devices/safety-communications/pulse-oximeter-accuracy-and-limitations-fda-safety-communication |
| S17 | FDA General Wellness, 6 Jan 2026 — no disease-named thresholds | https://www.fda.gov/regulatory-information/search-fda-guidance-documents/general-wellness-policy-low-risk-devices |
| S18 | AHA resting HR / tachycardia / bradycardia; HF daily-weight patient education | cited with URLs in [alarm_catalogue.md](alarm_catalogue.md) §3 |
| S19 | WHO SpO2 bands; NSF sleep bands | `HealthReferenceRanges` |

S6 was retrieved as a 2026 preprint that used Fitbits in an HF population;
treat the *direction* (steps down, RHR up before a visit) as the finding,
not the DOI's venue.

---

**Related:** [alarm_catalogue.md](alarm_catalogue.md) ·
[alerting_algorithm_card.md](../compliance/alerting_algorithm_card.md) ·
[data_sync_architecture.md](data_sync_architecture.md) ·
[llm_design.md](../llm_design.md) ·
[health-data.md](../execution/backend/api/health-data.md) ·
[research/queue/2026-09-04-google-health-api-cardiac-scopes.md](../../research/queue/2026-09-04-google-health-api-cardiac-scopes.md)

**Last updated:** 2026-09-20
