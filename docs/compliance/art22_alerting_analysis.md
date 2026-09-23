# Art. 22 Analysis & Model Validation Plan — Automated Alerting

> **Status: DRAFT — pending review by a qualified privacy professional.** Prepared 2026-08-10
> against the code as merged that day (the original baseline: §1–§6 as first written);
> **revised 2026-09-23** against `main` as merged that day — §1 producer table, the §2.1 re-run,
> V2b in §5 and the §6 verdict table are the as-built analysis. This document discharges the *drafting* half of DPIA
> risk **R-B1** ("Art. 22 analysis; human-review pathway; documented model validation") for the
> alerting that now exists; the *execution* half — running the validation protocol in §5 and
> recording results — remains outstanding and **gates prod alerting** (production setup
> runbook, step 10). Like the [DPIA](dpia.md), every factual claim cites the repository;
> nothing here is legal advice.

## 1. What is being analysed

Four automated alert producers exist. The two **LLM-routed producers are dev-only** (they run
in the pipeline's assessor job, gated on `enable_pipeline_jobs`, which prod has off); the two
**deterministic producers run in every environment**. All alert a test-user population today:

| Producer | Nature | Alert | Code |
|---|---|---|---|
| Real-time assessor | **LLM-routed**: SSA features → MedGemma verdict → severity parse | `HeartRate` red/orange | `RealtimeAssessmentService`, `AssessmentSeverityParser` |
| Inactivity detector | Deterministic rule (device silence) | `Inactivity` yellow | `InactivityDetectionService` |
| Statistical engine (R1) | Eleven deterministic rules produce *findings* — nine **comparative** rules vs the 30-day baseline (silent without one) and two **measured** rhythm rules (`irregular_rhythm`, `ecg_afib`, DPIA A26) that relay a finding the wearer's device already classified and are deliberately not gated on a baseline; **since 2026-09-19 LLM-routed** — MedGemma returns the severity, headline and message for every finding (`CARDITRACK_STATISTICAL_JUDGEMENT_PROMPT`) | yellow/orange/red per the model's verdict, mapped strictly, fail closed | `StatisticalAlertRules`, `StatisticalAlertService` (pipeline `assess` job) |
| Caregiver-defined alarms (R2) | Deterministic threshold arithmetic, **on numbers the caregiver chose** | yellow/orange/red, chosen by the caregiver | `MetricAlarmEvaluator`, `MetricAlarmEngine` |

The first and third involve a model; the other two are pure arithmetic against the member's own
baseline (inactivity in the Worker, alarms on the caregiver's own numbers). All four produce the
same artifact: an `Alert` row a caregiver sees, acknowledges,
and resolves. Since 2026-08-11, Red/Orange alerts are additionally **dispatched by push (FCM
HTTP v1 with APNs passthrough)** with a 120s/300s/900s escalation ladder (re-push → fan-out to
other caregivers → `UNDELIVERED_CRITICAL`) and quiet hours. **No alert triggers any action
beyond notifying humans** — there is no SMS fallback, no effect on any service, price, or
entitlement; the escalation ladder only widens *who is notified*, never what is done.

## 2. Does Art. 22(1) apply?

Art. 22(1) covers decisions "based solely on automated processing … which produce legal
effects concerning [the data subject] or similarly significantly affect" them.

**Analysis:**

- **"Solely automated"** — the alert *generation* is automated, but the decision with
  real-world effect — whether anyone checks on the wearer — is made by a human caregiver
  reading the alert. The system informs; it does not act. The EDPB's guidance treats human
  involvement as meaningful only where the human has authority and competence to depart from
  the recommendation: here the caregiver is not reviewing the machine's decision, they *are*
  the decision-maker, with full context the machine lacks (they can see the wearer).
- **"Similarly significant effect"** — a false negative (missed anomaly) does not change the
  wearer's position relative to not having the product; a false positive costs a check-in
  call. The DPIA (row B1) conservatively flagged the *designed* pipeline as ADM with
  significant effect — that design included SMS dispatch (still absent) and push escalation
  (**now built** — see the fired-trigger note below). What is built is an information service
  to a human intermediary, but one that now reaches lock screens and escalates unacknowledged
  Safety/Red deliveries.

**Conclusion (draft):** as built, the alerting most likely falls **outside** Art. 22(1) —
there is no solely-automated decision with significant effect; there is automated *profiling*
(Art. 4(4)) feeding a human decision, which engages transparency duties (Arts. 13–15) but not
the Art. 22 prohibition. **However**, this analysis adopts the conservative posture of
treating Art. 22-grade safeguards as required anyway, because (a) the boundary shifts the
moment push/SMS dispatch lands (a notification that wakes a family at 3am moves toward
"similarly significant"), and (b) the population is vulnerable (DPIA §2 criterion 7).

> **Trigger FIRED — 2026-08-11.** The stated re-run condition ("before enabling push
> dispatch") was overtaken: the push delivery spine shipped on 2026-08-11, so push dispatch
> is now enabled and the boundary analysis in §2 was written against a system that no longer
> exists. **Re-run drafted 2026-09-23 — see §2.1.** SMS remains absent.

### 2.1 Re-run — 2026-09-23, against the as-built system

Drafted alongside the [AI Act classification](ai_act_classification.md), which reuses this
section's facts under a different test. Four things changed since §2 was written against the
2026-08-10 code:

1. **Push dispatch and the escalation ladder** (2026-08-11; DPIA A18). *Solely automated?* No
   change: the ladder alters who is notified and how insistently — re-push, fan-out to the
   member's other caregivers, then an `UNDELIVERED_CRITICAL` record — never what is done. The
   terminal state is a stored fact, not an action on anyone. *Similarly significant?* A push that
   wakes a caregiver is a real intrusion, but it lands on the person who registered the device
   and chose the categories and quiet hours (`NotificationPreferences`); the wearer's position
   relative to not having the product is unchanged. **§2's conclusion holds.**
2. **R1 severity, headline and message are now MedGemma's** (2026-09-19; DPIA A15; V4 entry in
   §5). The model is in the severity path of the main producer, not only the dev-only heart-rate
   assessor. This **widens the profiling footprint** — every alert from the nine comparative
   rules is now a model verdict on a person's deviation from their own pattern (the two measured
   rhythm rules relay the device's own classification and are judged for severity only, so they
   add model-written words but no new profiling) — and with it the Arts. 13–15 duty to explain
   the logic. It does not move the Art. 22 test: the output is still an `Alert` row awaiting a named
   caregiver's acknowledgment, the eleven rules still decide whether a finding reaches the
   model at all (nine baseline thresholds, algorithm card §2; two device-measured rhythm
   findings, DPIA A26), and the fail-closed parse and strict mapping carry over (§3). R1 rows
   now fall under **V2b** and V3 (§5) — V2 as written cannot cover them, see V2b.
3. **Member chat can change alert settings** (2026-09-17; DPIA A20 restated). A model reads
   which rule or alarm the caregiver meant; the change is proposed in one turn and applied only
   on the caregiver's explicit yes in the next, through the same services and primary-caregiver
   check the settings pages use. The human confirms every write, so this **strengthens** §2's
   position in the same way caregiver-defined alarms did (§5, 2026-09-06). The residual risk is
   a misread intent, mitigated by the proposal being composed in code from the parsed request
   and shown before the yes.
4. **Prod runs neither LLM-routed producer** (`infrastructure/environments/prod.tfvars`,
   `enable_pipeline_jobs = false`); only the Worker's inactivity detector and caregiver-defined
   alarms run there. The conclusions above describe dev, and prod after the flag flips — which
   is why the flip is now a DPIA §13 trigger.

**Re-run conclusion (draft):** the as-built alerting most likely remains **outside Art. 22(1)**
— there is still no solely-automated decision with significant effect, and the human in the
loop is the decision-maker, not a reviewer. The conservative posture (Art. 22-grade safeguards
regardless) is retained. One new transparency observation: since item 2, the words a caregiver
reads on an alert are the model's. The detail screen already names the yardstick in code
(`AlertEvidenceComposer`) and shows the model's narrative in its own card; what it lacks is a
line saying the headline and message were model-written — the same product-surface disclosure
the AI Act's Art. 50 asks for, tracked with the classification document. **Still pending
review by a qualified privacy professional.**

## 3. Safeguards already engineered (with citations)

The Art. 22(3)-style safeguard set — suitable measures, right to human intervention, right to
contest — maps onto controls that exist in code today:

| Safeguard | Implementation | Where |
|---|---|---|
| The model cannot alert by mumbling | Only an explicit closing `Severity:` line routes; an unparseable verdict is stored with null severity and **never** alerts | `AssessmentSeverityParser`; `RealtimeAssessmentService` |
| The model never computes, only interprets | Every number (trend, deviation, baselines) is deterministic in-process .NET; SSA eigen-decomposition is Math.NET Numerics (`SsaParameters.Engine = "MathNet.Numerics.Evd"`), grouping and residual stay CardiTrack code; calibrated numeric risk scores were **descoped with the LSTM** (2026-08-10), deleting DPIA R-B2's worst exposure | `ISsaDecomposition` / `SsaDecomposition`, `StatisticalAlertRules`; llm_design "Trend Interpretation"; [mathnet_numerics.md](../technical/mathnet_numerics.md) |
| No alarms from statistically thin evidence | Provisional (7/14-day) baselines never alert — enforced by fetching only the established 30-day baseline | `StatisticalAlertService` |
| Absence of data never reads as an event | Null-vs-zero discipline; the one red rule requires a **measured** zero | `StatisticalAlertRules.NoMorningActivity` |
| Alarm fatigue is bounded | One unresolved alert per remedy (cooldowns); exactly-once severity routing under concurrency; per-day dedup | `AlertRuleMarkers`; assessment upsert claim |
| Human intervention is structural | The alert lifecycle **is** the human-review pathway: every alert awaits acknowledgment by a named caregiver; resolution is recorded (`AcknowledgedByUserId`, `IsResolved`) and is what re-arms the producer | `Alert` entity; `AlertsController` |
| Contestability / auditability | Every alert carries its evidence: the `rule` discriminator plus the numbers that fired it in `MetricValues`; assessments store model input features, full output, raw severity word, routed severity, and **`SsaEngine`** — reconstructable months later | `RealtimeAssessment`; alert producers |
| Transparency to the reader | Alert text names the observation; the detail screen names **the yardstick itself**, composed in .NET from the figures the producer stamped (`AlertEvidenceComposer`) — including the rule's own name on the settings screen, the line the reading crossed, and the window the "usual" was learned over. The model's explanation of the same alert sits beside it in a separate card, written by the pass that raised the alert and served from a stored row | alerts.md; `AlertEvidenceComposer`; `AlertDetailResponse.Evidence` / `.Narrative`; `GET /api/v1/insights/alerts/{alertId}` |
| The human sets the threshold, not only reads the result | Caregiver-defined alarms (R2) move the judgement itself to the human: they choose the metric, the level, the window and how urgent it is, and the system does arithmetic on their instruction. This is a **strengthening** of §2's position rather than a new exposure — but see the V4 entry below, because the machinery acting on that instruction is still ours | `MetricAlarm`; [alarm_catalogue.md](../technical/alarm_catalogue.md) |

**Gap acknowledged:** the DPIA's "human-review queue for Critical" beyond the caregiver (a
staff/clinical review tier) does not exist and is not planned — CardiTrack is explicitly not
clinical-grade and never diagnoses (llm_design). The position taken here: the caregiver *is*
the review tier for a family product; a clinical tier would change the product's regulatory
posture entirely (DPIA OI-2). `[DECISION REQUIRED]` — confirm or reject this position at
sign-off.

## 4. Transparency obligations (applies regardless of Art. 22)

Profiling exists (baselines, assessments), so Arts. 13–15 require telling users meaningful
information about the logic involved. Current state: the in-app alert text explains each
alert's basis; `/privacy` has a plain-language **How alerting works** section (shipped
thresholds only — no sensitivity slider). **That parenthetical is now out of date and the page
needs updating**: caregiver-defined alarms ship in R2, so the public explanation of how alerting
works has to say that a caregiver can add thresholds of their own, and that CardiTrack's own rules
are unaffected by them. The auditor-facing artefact is
[alerting_algorithm_card.md](alerting_algorithm_card.md). Remaining privacy-policy work
(Google Limited Use, Art. 14 wearer notice, emergency-contact notice) is still DPIA
mitigation **M12** / §6.4, not duplicated here.

## 5. Model validation protocol (to execute before prod alerting)

R-B1 requires "documented accuracy/false-negative testing". The LLM-routed producer is the
subject; the deterministic producers are validated by their boundary tests (34 rule tests in
`StatisticalAlertRulesTests` et al.) and need no model validation.

**V1 — Contract conformance (done, continuous):** severity-parse strictness and fail-safe
behavior are pinned by unit tests (parser suite; assessor suite). Any regression fails CI.

**V2 — Retrospective benchmark (to run, dev data):** for every stored `RealtimeAssessment`
(90-day window), compute a rule-based reference verdict from the same stored features
(deviation-score bands agreed with the thresholds in alerts.md). Report: agreement rate,
false-negative rate (model green/yellow where reference says red/orange), false-positive
rate, **split by age band and sex** (the per-cohort requirement). Acceptance to propose at
sign-off: FN rate on reference-red windows ≈ 0 within the sample; FP consistent with the <5%
product target.

**V2b — Retrospective benchmark for the model-judged R1 path (to run, dev data; added
2026-09-23):** V2 cannot cover R1 because its negative class does not exist: a finding the model
judges benign is written nowhere (`StatisticalAlertService` remarks — deliberate, so the finding
is re-judged as the day's readings arrive), so stored rows are alerts only. Protocol: (a) for
every R1 `Alert` row since 2026-09-19, compare the model's severity with the rule's former
constant (the lineage column in the algorithm card §2), by rule, **age band and sex** —
agreement, escalation and de-escalation rates, with every de-escalation of a former red read
individually; (b) a **shadow log of judged findings including benign verdicts** (rule, finding
values, raw and mapped severity) for a bounded period in dev — the "judged-day marker" the
service's own remarks name as the follow-up — which gives V2b its negative class and lets the
false-negative rate be estimated the way V2 does for the assessor; (c) the two measured rhythm
rules are reported separately, since their finding is the device's classification and the only
question for the model is severity. Acceptance to propose at sign-off, as V2's.

**V3 — Prod shadow period (to run at enablement):** enable the pipeline's assessor job in prod
with alert audience restricted to staff-owned test members for ≥2 weeks; measure alert volume,
FP rate (staff adjudication), and cooldown behavior under real load before any real family is
enrolled. Since 2026-09-23 this covers **both LLM-routed producers**: the assessor's heart-rate
alerts and the R1 judged alerts alike are adjudicated as warranted or not, reported per rule and
per cohort (age band, sex). This slots between runbook steps 6 and 10's lift.

**V4 — Change control (standing):** any change to a `CARDITRACK_*` prompt, the model tag,
the severity mapping, **or the numerical engine that produces SSA features / baseline
formulas** (`SsaParameters.Engine`, `BaselineCalculator` mean/σ vs robust alternatives)
re-triggers V2 (the DPIA already names prompt-content changes as a §13 review trigger;
numerical-engine changes were added 2026-08-14). The model tag is pinned (`Q4_K_M`, one
tag across environments) and the SSA engine is pinned (`MathNet.Numerics.Evd`) precisely so
an assessment means the same thing everywhere. The 2026-08-14 Jacobi → Math.NET EVD swap is
a V4 event: stored `HrDeviationScore` values from before that date are the same algebra, a
different solver, and must not be treated as bit-stable against post-swap rows.

**V4 entries recorded:**

| Date | Change | Assessment |
|---|---|---|
| 2026-08-14 | Jacobi → Math.NET EVD for SSA | Same algebra, different solver. Stored `HrDeviationScore` values are not bit-stable across the swap and must not be pooled in a V2 claim |
| 2026-09-06 | **Caregiver-defined alarms** (`MetricAlarm`) — a fourth producer whose thresholds are set by the user | See below |
| 2026-09-19 | **Statistical rules become findings; MedGemma returns the verdict** (`CARDITRACK_STATISTICAL_JUDGEMENT_PROMPT`, `StatisticalAlertService`, moved from the Worker to the pipeline's assessor job) | Thresholds in the algorithm card's §2 are unchanged and still decide *whether a finding is put to the model*; what changed is who decides the severity and writes the copy. Every R1 alert row from this date carries a model verdict, so pre/post rows must not be pooled in a V2 claim about R1 severities. The severity mapping, parser strictness and fail-closed behaviour are the assessor's, pinned by the same unit-test contract (V1). The R1 rows now fall under the LLM-routed producer's validation (**V2b**/V3, added 2026-09-23), not the boundary-test exemption above |

**On the 2026-09-06 change.** It does not alter any threshold in the algorithm card's §2: the nine
statistical rules run unchanged, and `AlertSensitivity` still drives nothing. What it adds is a
producer that evaluates a number the caregiver chose. Three observations for whoever reviews this:

1. **The Art. 22 posture improves rather than degrades.** §2's argument is that the caregiver is the
   decision-maker rather than a reviewer of the machine's decision. When the caregiver also sets the
   threshold, the automated part shrinks to arithmetic on their own instruction — closer to a kitchen
   timer than to profiling. The *profiling* that engages Arts. 13–15 is unchanged, because the
   baseline-relative threshold kinds still compare a person against their own learned pattern.
2. **The machinery is still ours, and still reviewable.** The evaluator's arithmetic, the M-of-N
   semantics, the missing-data verbs, the hysteresis band, and above all the **bounds on what a
   threshold may be** are product decisions, not user ones. A change to any of them is a V4 event on
   the same footing as a change to a built-in constant. Recorded in the algorithm card §6.
3. **The bounds are a safeguard, not a limitation.** Clamping each metric's threshold to a band is
   what stops a caregiver building an alarm that pages them nightly and then abandoning alerting
   altogether. The reasoning, and the one place where the bound deliberately departs from the
   clinical definition (bradycardia), are documented in
   [alarm_catalogue.md](../technical/alarm_catalogue.md) §3.1 and §7.

**The §2 re-run that push dispatch triggered on 2026-08-11 was drafted 2026-09-23 (§2.1);** it
remains unreviewed by a qualified privacy professional, as does the rest of this document.

**Results ledger:** append V2/V3 results to this document when executed; prod alerting for
real families is gated on both being recorded here.

## 6. Verdict summary

| Question | Answer |
|---|---|
| Is built alerting Art. 22(1) ADM? | Most likely **no** (human decision-maker, no significant automated effect) — treated conservatively as if yes |
| Are Art. 22(3)-grade safeguards present? | Yes — engineered and cited above; one deliberate gap (`no clinical review tier`) flagged for sign-off |
| What blocks prod alerting? | Executing V2 + V3 and recording results here; sign-off by a qualified reviewer. Privacy-policy alerting text and the algorithm card now exist; the rest of `/privacy` is still a short placeholder |
| What re-opens this analysis? | Push dispatch (2026-08-11) fired the first re-run, drafted 2026-09-23 (§2.1). Next: **enabling the pipeline jobs in prod**; SMS dispatch landing; any prompt/model/severity-mapping change; wearer-population change (e.g. exceeding the 100-user cap); any of the re-classification events in [ai_act_classification.md](ai_act_classification.md) §6.3 |
