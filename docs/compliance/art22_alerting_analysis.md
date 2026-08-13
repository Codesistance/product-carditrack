# Art. 22 Analysis & Model Validation Plan — Automated Alerting

> **Status: DRAFT — pending review by a qualified privacy professional.** Prepared 2026-08-10
> against the code as merged that day. This document discharges the *drafting* half of DPIA
> risk **R-B1** ("Art. 22 analysis; human-review pathway; documented model validation") for the
> alerting that now exists; the *execution* half — running the validation protocol in §5 and
> recording results — remains outstanding and **gates prod alerting** (production setup
> runbook, step 10). Like the [DPIA](dpia.md), every factual claim cites the repository;
> nothing here is legal advice.

## 1. What is being analysed

Three automated alert producers exist. The **LLM-routed producer is dev-only** (gated on
`enable_pipeline_jobs`); the two **deterministic Worker producers run in every environment
the Worker deploys to**. All alert a test-user population today:

| Producer | Nature | Alert | Code |
|---|---|---|---|
| Real-time assessor | **LLM-routed**: SSA features → MedGemma verdict → severity parse | `HeartRate` red/orange | `RealtimeAssessmentService`, `AssessmentSeverityParser` |
| Inactivity detector | Deterministic rule (device silence) | `Inactivity` yellow | `InactivityDetectionService` |
| Statistical engine (R1) | Five deterministic rules vs 30-day baseline | yellow/orange/red per taxonomy | `StatisticalAlertRules`, `StatisticalAlertService` |

Only the first involves a model; the other two are pure arithmetic against the member's own
baseline. All three produce the same artifact: an `Alert` row a caregiver sees, acknowledges,
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
> exists. **The re-run is an open action**, feeding DPIA §13 (recorded in DPIA v0.6's
> changelog); until it is done, §2's "most likely outside Art. 22(1)" conclusion should be
> treated as unreviewed for the as-built system. SMS remains absent.

## 3. Safeguards already engineered (with citations)

The Art. 22(3)-style safeguard set — suitable measures, right to human intervention, right to
contest — maps onto controls that exist in code today:

| Safeguard | Implementation | Where |
|---|---|---|
| The model cannot alert by mumbling | Only an explicit closing `Severity:` line routes; an unparseable verdict is stored with null severity and **never** alerts | `AssessmentSeverityParser`; `RealtimeAssessmentService` |
| The model never computes, only interprets | Every number (trend, deviation, baselines) is deterministic .NET; calibrated numeric risk scores were **descoped with the LSTM** (2026-08-10), deleting DPIA R-B2's worst exposure | `SsaDecomposition`, `StatisticalAlertRules`; llm_design "Trend Interpretation" |
| No alarms from statistically thin evidence | Provisional (7/14-day) baselines never alert — enforced by fetching only the established 30-day baseline | `StatisticalAlertService` |
| Absence of data never reads as an event | Null-vs-zero discipline; the one red rule requires a **measured** zero | `StatisticalAlertRules.NoMorningActivity` |
| Alarm fatigue is bounded | One unresolved alert per remedy (cooldowns); exactly-once severity routing under concurrency; per-day dedup | `AlertRuleMarkers`; assessment upsert claim |
| Human intervention is structural | The alert lifecycle **is** the human-review pathway: every alert awaits acknowledgment by a named caregiver; resolution is recorded (`AcknowledgedByUserId`, `IsResolved`) and is what re-arms the producer | `Alert` entity; `AlertsController` |
| Contestability / auditability | Every alert carries its evidence: the `rule` discriminator plus the numbers that fired it in `MetricValues`; assessments store model input features, full output, raw severity word, and routed severity — reconstructable months later | `RealtimeAssessment`; alert producers |
| Transparency to the reader | Alert text names the observation and the yardstick in plain language; the on-demand insight endpoint explains any alert | alerts.md; `GET /api/v1/insights/alerts/{alertId}` |

**Gap acknowledged:** the DPIA's "human-review queue for Critical" beyond the caregiver (a
staff/clinical review tier) does not exist and is not planned — CardiTrack is explicitly not
clinical-grade and never diagnoses (llm_design). The position taken here: the caregiver *is*
the review tier for a family product; a clinical tier would change the product's regulatory
posture entirely (DPIA OI-2). `[DECISION REQUIRED]` — confirm or reject this position at
sign-off.

## 4. Transparency obligations (applies regardless of Art. 22)

Profiling exists (baselines, assessments), so Arts. 13–15 require telling users meaningful
information about the logic involved. Current state: the in-app alert text explains each
alert's basis; **a plain-language "how alerting works" section in the privacy policy does not
exist yet** — the Web app's `/privacy` page is a placeholder. Carried as an open item to
the privacy-policy work (DPIA mitigation **M12**), not duplicated here.

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

**V3 — Prod shadow period (to run at enablement):** enable the assessor in prod with alert
audience restricted to staff-owned test members for ≥2 weeks; measure alert volume, FP rate
(staff adjudication), and cooldown behavior under real load before any real family is
enrolled. This slots between runbook steps 6 and 10's lift.

**V4 — Change control (standing):** any change to a `CARDITRACK_*` prompt, the model tag, or
the severity mapping re-triggers V2 (the DPIA already names prompt-content changes as a §13
review trigger). The model tag is pinned (`Q4_K_M`, one tag across environments) precisely so
an assessment means the same thing everywhere.

**Results ledger:** append V2/V3 results to this document when executed; prod alerting for
real families is gated on both being recorded here.

## 6. Verdict summary

| Question | Answer |
|---|---|
| Is built alerting Art. 22(1) ADM? | Most likely **no** (human decision-maker, no significant automated effect) — treated conservatively as if yes |
| Are Art. 22(3)-grade safeguards present? | Yes — engineered and cited above; one deliberate gap (`no clinical review tier`) flagged for sign-off |
| What blocks prod alerting? | Executing V2 + V3 and recording results here; the privacy-policy transparency text; sign-off by a qualified reviewer |
| What re-opens this analysis? | **Push dispatch landed 2026-08-11 — this trigger has FIRED and the re-run is an open action (see §2, DPIA §13).** Also: SMS dispatch landing; any prompt/model/severity-mapping change; wearer-population change (e.g. exceeding the 100-user cap) |
