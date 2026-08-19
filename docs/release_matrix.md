# CardiTrack Release Matrix

**The canonical release plan.** When any other document (manifest roadmap, UI screen specs, API priorities) disagrees with this matrix, this matrix wins. Platform docs keep their own MVP numbering (mobile has 3 MVPs, web has 4); this matrix maps them onto shared release waves.

## Release Waves

| Wave | Roadmap window | Mobile release | Web release | Theme |
|------|---------------|----------------|-------------|-------|
| **R1 — Core Monitoring** | Q4 2026 (MVP completion + beta) | Mobile MVP 1 | Web MVP 1 | Sign up, add CardiMembers, connect Fitbit, dashboard, all alert types, acknowledgment |
| **R2 — Management & Billing** | Q1 2027 (public launch) | Mobile MVP 2 (part) | Web MVP 2 | Trend charts, notification preferences, **subscriptions/billing**, Garmin, expanded exports; **AI pipeline rollout** on GCP — Pub/Sub + Cloud Run ([llm_design.md](./llm_design.md)) |
| **R3 — Family & Multi-Member** | Q2 2027 | Mobile MVP 2 (part) | Web MVP 3 | Family invitations, shared notes, multi-member management, test-result scanning |
| **R4 — Native & Offline** | Q3 2027 | Mobile MVP 3 | Web MVP 4 | Biometric login, offline support, push actions, widgets/PWA, SNOMED CT export |

API endpoint priorities (P0–P2 in [/execution/backend/api/](./execution/backend/api/readme.md)) are **relative to the wave in which the feature ships**, per the matrix below.

## Feature Matrix

Legend: wave number = ships in that wave; — = not planned for that surface. **Status** is as of **August 14, 2026**: ✅ Shipped · 🔶 In progress / partial · ⬜ Not started.

| Feature | API | Mobile | Web | Plan gate | Status (Aug 14, 2026) |
|---------|-----|--------|-----|-----------|----------------------|
| Auth0 Universal Login (email + password) | R1 | R1 | R1 | — | ✅ Shipped, incl. email-verification gate |
| Social sign-in (Google / Apple via Auth0) | R1 | R1 | R1 | — | 🔶 **Mobile wired** (Auth0 PKCE via system browser; cancel restores button opacity). Web pending. Production tenant social connections remain an ops gate |
| Onboarding (atomic account + org + CardiMember setup) | R1 | R1 | R1 | — | ✅ Shipped, incl. org-orphaning cleanup fix (PR #5) and sex capture at M1-04 |
| CardiMember CRUD + profile | R1 | R1 | R1 | Member limit by tier | 🔶 API + mobile shipped (GET/PUT/DELETE `/api/v1/cardimembers/{id}` + detail/edit screens, member Phone on M1-14); web not started (template-stage). Tier member limit not yet enforced |
| Emergency contacts, medical notes (encrypted) | R1 | R1 | R1 | — | 🔶 API + mobile shipped inside member CRUD (medical notes AES-encrypted at rest); web not started (template-stage) |
| Consent recording (per-metric) | R1 | R1 | R1 | — | ⬜ Not started |
| Fitbit connection (Google Health API — server OAuth + REST client) | R1 | R1 | R1 | **100 connected wearers max until Google verification passes** | ✅ Client shipped (PR #10, migrated off legacy Fitbit Web API); registration done 2026-08-07; 🔶 live-wearer field verification pending |
| Google Health webhook subscriptions (push ingestion) | R2 | R2 | R2 | — | 🔶 **Shipped in dev ahead of R2** — receiver live (`POST /webhooks/google-health`) + Subscriber registered 2026-08-10, feeding Pub/Sub + the aggregator; prod off until the pipeline rollout. 10-minute Worker polling (✅ shipped) remains the guaranteed fallback |
| Device management (status, primary, reconnect, remove) | R1 | R1 | R1 | — | 🔶 API + mobile shipped (remove / set-primary / sync / refresh endpoints + device management screen, M1-15); web not started (template-stage) |
| Dashboard + daily health summary | R1 | R1 | R1 | — | 🔶 Per-member dashboard endpoint + mobile dashboard shipped (unresolved-alerts strip, weather popup, delayed "Loading", single-sentence AI status, 30-day baseline gate); web dashboard not started (web app is still template-stage) |
| Statistical alerts (all 5 launch types) + acknowledgment/notes | R1 | R1 | R1 | — | 🔶 **Shipped** via `StatisticalAlertWorker` (PR #118) and `InactivityDetectionWorker` (PR #116); `AlertsController` (6 routes incl. GET detail + undo-ack) + `AlertsPage` (M1-10) + `AlertDetailPage` (M1-11/12/16, rule-specific chart) serve them. Red/Orange alerts push via the delivery spine; Safety-class nudges (`DEVICE_AUTH_BROKEN`, three-tier `DEVICE_BATTERY_LOW`) also push ([notification_engine.md](./technical/notification_engine.md)). Notes/photos not started. SSA eigen-decomposition is in-process Math.NET ([mathnet_numerics.md](./technical/mathnet_numerics.md)). *Caveat: `InactivityDetectionWorker` was throwing on every tick until fixed 2026-08-13 — inactivity alerting was silently dead before that date* |
| AI insights + chat endpoints (MedGemma via Ollama on Cloud Run; Gemini 2.0 Flash) | R1 | R1 | R1 | — | ✅ Shipped (synchronous endpoints; the R2 event-driven pipeline is separate) |
| Reports (health report generation) | R1 | R1 | R2 | Complete Care | 🔶 Text-only generation shipped; PDF/CSV/FHIR R4 formats not started |
| **Data-completeness notifications (in-app)** | R1 | R1 | R3 | — | ✅ **Shipped** — detection worker, 8 rules, inbox + dashboard card + safety banners + mute management. In-app only by decision; the staleness rule defers to `InactivityDetectionWorker`'s faster device-silence alert ([notification_engine.md](./technical/notification_engine.md)) |
| Push notification registration | R1 | R1 | — | — | ✅ **Shipped** — brought forward from its original R2 placement ([notification_engine.md](./technical/notification_engine.md) Phase 3). FCM HTTP v1 relay (APNs passthrough), device tokens, delivery outbox, 120s/300s/900s escalation ladder, quiet hours. iOS notification service extension deferred (needs Mac-based CI verification this environment doesn't have) |
| Health data export — PDF, CSV, FHIR R4 | R1 | R1 | R2 | Complete Care | ⬜ Not started (see reports row for text-only interim) |
| Baseline learning progress | R1 | R1 | R1 | — | 🔶 Daily `BaselineCalculationWorker` + mobile learning screen shipped; web not started (web app is still template-stage) |
| Monitoring pause / resume | R1 | R1 | R1 | — | 🔶 API + mobile shipped (pause/resume endpoints; paused members excluded from sync scheduling); web not started (template-stage) |
| **Account deletion / erasure endpoint** | R1 | R1 | R2 | — | ⬜ **Not started — but the promise is already published.** privacy-policy §5 and the deletion page commit to erasure within **30 days of a verified request**, including inside the Google Health API section. Nothing delivers it: member and device deletes are **soft** (`IsActive` flip + token discard), no PHI row is removed, and with almost no foreign keys a deleted member **orphans** its `ActivityLogs`/`Alerts`/`PatternBaselines`/`DeviceConnections` rows, which stay live and queryable ([data_protection_architecture.md](./technical/data_protection_architecture.md) findings 5–6). Interim control: [manual_erasure_runbook.md](./technical/manual_erasure_runbook.md), viable only at the current sub-100-wearer cap. `ErasureWorker`, `erasure_requests`/`erasure_ledger` and `SubjectDataMap` are designed (P2/P3) and unbuilt |
| **30-day trial (no billing UI)** | R1 | R1 | R1 | — | ✅ Shipped — trial provisions the **Complete Care tier** for 30 days |
| Region-localized phone input (UK groundwork) | R1 | R1 | R1 | — | ✅ Shipped (PR #8) |
| Health-data disclosure (Google-mandated in-app disclosure) | R1 | R1 | R1 | — | 🔶 Web shipped (PR #9); **mobile missing — gate for public launch** |
| Observability (Datadog APM, opt-in metrics via `Apm:Engine`) | R1 | — | — | — | ✅ Shipped (PR #4) |
| **Google restricted-scope verification + annual CASA** | R1→R2 gate | R1→R2 gate | R1→R2 gate | **Blocks >100 connected wearers** | ⬜ Not started — cross-wave external gate: Gate 1 Trust & Safety review + Gate 2 annual CASA ($500–$4,500, 2–6 weeks; combined runway 4–8 weeks). **Now four scopes to justify, not three** — `settings.readonly` added 2026-08-13 (PR #262) for wearable battery; added deliberately *before* submission, since a scope added afterwards needs its own second review. See [user_onboarding_process.md Step 6](./technical/user_onboarding_process.md), [oauth_clients.md](./technical/oauth_clients.md) and [runbook step 1b](./technical/production_setup_runbook.md) |
| Wearable battery level + `DEVICE_BATTERY_LOW` safety notification | R1 | R1 | — | Needs `settings.readonly` granted | 🔶 Code, migration and Terraform shipped (PR #262); **three tiers** (Warning ≤30% / Urgent ≤20% / Critical ≤10% or Empty band), freshness **12 hours**, Safety-class push. **Blocked on the console scope change** (runbook step 1b) in both devices projects. Existing connections report no battery until the wearer reconnects — degrades silently by design, never fails a sync |
| **Legacy Fitbit Web API sunset — September 2026** | external deadline | external deadline | external deadline | — | 🔶 Hard external deadline (~4 weeks away). Code migrated to Google Health API (PR #10); console registration done 2026-08-07; field mappings verified against the v4 discovery document 2026-08-09 (two silent-zero defects found and fixed); **blocking task: live-wearer check that each type is actually populated** |
| **CardiJournal — the Weekbook** | R2 | R2 | — | Complete Care | ⬜ **Not started — but sold.** The pricing page lists a weekly round-up under Complete Care as of 2026-08-18. Only the daily `DigestAudience.Daybook` exists (own row above, prod-gated). The Weekbook needs its own audience value, its own due rule and its own prompt — a week is not a longer day, and the daily prompt's register does not carry over unexamined. **It is a raw reassessment of the week from that week's measurements, not a summary of the seven Daybooks** — so it still gets written for a week whose Daybooks were skipped or discarded |
| **CardiJournal — the Monthbook** | R2 | R2 | — | Guardian Plus | ⬜ **Not started — but sold.** Same as the Weekbook row, listed under Guardian Plus, and likewise a raw reassessment of the month rather than a digest of Weekbooks. Note two interactions: minute-grain readings are dropped at 90 days and hourly rollups at 13 months, so a monthly entry composed months later can only draw on what survives the partition drops; and `DigestRetentionMonths = 3` (Worker `PartitionMaintenanceOptions`) is **shorter than the 180-day history Guardian Plus sells** — raising it is a prerequisite, and moves the published retention schedule in the DPIA, the data-protection ADR and the website privacy policy |
| Trend charts (7d/30d/90d/custom) | R2 | R2 | R2 | — | ⬜ Not started |
| Notification preferences (global + per-member, quiet hours, sensitivity) | R2 | R2 | R2 | — | 🔶 Global quiet hours + lock-screen detail + per-category mute shipped ahead of schedule alongside the push spine ([notifications.md](./execution/backend/api/notifications.md)); per-member scoping and sensitivity tuning not started |
| **Subscriptions & billing (Stripe)** | R2 | R2 | R2 | — | ⬜ Not started (no Stripe integration exists yet) |
| Garmin connection | R2 | R2 | R2 | — | ⬜ Not started |
| **AI pipeline** (Pub/Sub → SSA → MedGemma on Cloud Run; `long_term_trend` alerts; digests) | R2 | R2 | R2 | Advanced alerts: Complete Care | 🔶 **Shipped in dev ahead of R2** — digest, aggregator, assessor and webhook receiver all live on Pub/Sub + Cloud Run; prod enablement pending the MedGemma prod deploy. LSTM dropped 2026-08-10 — SSA pre-processing + prompt-injected reference ranges instead ([llm_design.md](./llm_design.md); decision log #7) |
| Export — HL7 v2 | R2 | R2 | R2 | Complete Care | ⬜ Not started |
| Family summaries / digests (half-hourly MedGemma summary + history) | R2 | R2 | R2 | — | 🔶 **Shipped in dev ahead of R2** — append-only `DigestEntries` with history, read via the insights digest endpoints; prod gated with the pipeline. Now two audiences: the running `Family` summary and the once-daily `Daybook` (own row below) |
| **CardiJournal — the Daybook** (once-daily account of a finished day, from all of the day's data, clinical register) | R2 | R2 | — | — | 🔶 **Shipped in dev ahead of R2** — `DigestAudience.Daybook` entries written by the digest job at 02:00 in the member's local time, one per day, never recomputed; read via `?audience=daybook` on the insights digest endpoints (with `search`/`from`/`to`/`urgency` filters, applied before the page cap) and listed on the mobile **Journal** tab (labelled CardiJournal in the page header; it replaced the Family stub); each entry opens its own page with source-tagged fortnight charts (NSF/AHA bands named) and deterministic awareness counts — counts, never risk scores, per decision log. Register allows a precise term where it explains itself; conditions, diagnoses and treatment refused in code (`DaybookPrompt`) |
| Real-time heart-rate assessment (SSA → MedGemma severity verdict) | R2 | R2 | R2 | — | 🔶 **Shipped in dev ahead of R2** — 5-minute SSA-gated assessor over the granular store; MedGemma only on a jump; red/orange verdicts create alerts and enqueue push; prod gated with the pipeline |
| Family questionnaires (digest-proposed questions + answers) | R2 | R2 | — | — | ✅ Shipped — `MemberQuestionnaires` + questionnaires endpoints; standing vs momentary (`QuestionnaireScope`); gap-backed asking (unresolved alert / Yellow+ observation, 12-hour ceiling); dismissed and answered-permanent never re-asked; in-card delete with confirm |
| Environmental enrichment (weather/AQI context for GPS exercise sessions) | R3 | R3 | — | — | 🔶 Built but **inert** — code, schema and consent flag shipped; no Cloud Run job or scheduler provisioned, and the `googlehealth.location.readonly` scope not yet requested |
| Granular minute-grain storage (hour vectors + rollups + retention) | R1 | R1 | R1 | — | ✅ Shipped — `GranularMetricHours`/`MetricRollupsHourly`, partitioned with retention by partition drop |
| Trend interpretation (family-facing narrative, no risk scores) | R3 | R3 | R3 | Complete Care | ⬜ Not started — replaces predictive monitoring; prediction cards were descoped 2026-08-10 with the LSTM |
| Family invitations + roles (admin/staff/viewer) | R3 | R3 | R3 | Family-member limit by tier | ⬜ Not started |
| Shared care notes + @mentions | R3 | R3 | R3 | — | ⬜ Not started |
| Multi-member comparison views | R3 | R3 | R3 | — | ⬜ Not started |
| Test-result scanning + insights | R3 | R3 | R3 | Complete Care | ⬜ Not started |
| Export — LOINC/CCD | R3 | R3 | R3 | Complete Care | ⬜ Not started |
| Activity/audit log endpoint (Admin) | R3 | R3 | R3 | — | ⬜ Not started |
| Biometric login (local gate) | R4 | R4 | — | — | ⬜ Not started |
| Offline support + sync queue | R4 | R4 | R4 (PWA) | — | ⬜ Not started |
| Push notification inline actions | R4 | R4 | R4 (browser) | — | ⬜ Not started |
| Home-screen widget / PWA install | R4 | R4 | R4 | — | ⬜ Not started |
| Apple Watch (on-device HealthKit bridge) | R3 | R3 | n/a | — | ⬜ Not started |
| Samsung Health connection | R3 | R3 | R3 | — | ⬜ Not started |
| Withings / Oura / Whoop | R4 | R4 | R4 | — | ⬜ Not started |
| Export — SNOMED CT | R4 | R4 | R4 | Complete Care | ⬜ Not started |
| Enterprise / Guardian Plus (business flow) | post-R4 | post-R4 | post-R4 | Guardian Plus | ⬜ Not started (out of MVP scope) |

## Resolved Conflicts (decision log)

1. **Subscriptions**: endpoints and UI ship together in **R2** — R1 (Q4 2026) is trial-only. The trial provisions the Complete Care tier for 30 days; R2 (Q1 2027) must land billing before the first beta trials convert, so trial-expiry handling is an explicit R2 entry criterion. (Previously: API P0 "launch blocker" vs UI specs MVP 2.)
2. **FHIR R4 export**: mobile ships it in R1, web in R2 — **deliberate**: mobile is the primary caregiver surface for doctor-visit prep.
3. **Export plan-gating**: all export formats require **Complete Care** (Basic has no export), consistent with [subscriptions.md](./execution/backend/api/subscriptions.md).
4. **`long_term_trend` alerts** require the AI pipeline and therefore ship in R2, not R1 — the five R1 alert types are statistical.
5. **AI severity taxonomy**: internal Critical/High/Medium/Low maps to user-facing red/orange/yellow/green everywhere ([llm_design.md](./llm_design.md)).
6. **Polling vs webhooks**: Worker polling **shipped as the R1 ingestion path** (originally 30-minute; default cadence reduced to **10 minutes** on Aug 9, 2026 — migration `ReduceDefaultSyncFrequencyToTenMinutes`) and remains the system of record for ingestion until R2. Webhook push subscriptions move to **R2**, delivered with the AI pipeline. The original R1 row bundled "server OAuth, webhooks" — that bundling is superseded; the matrix now splits them. **Superseded (2026-08-10): both the webhook path and the AI pipeline shipped in dev ahead of R2; prod remains gated.**
7. **AI pipeline platform**: the pipeline runs on **GCP — Pub/Sub + Cloud Run, with MedGemma served via Ollama on Cloud Run and Gemini 2.0 Flash for chat/reports** — superseding the earlier Azure Functions / Event Hubs design. This matches the deployed Terraform footprint (Cloud Run, Cloud SQL PostgreSQL, Secret Manager, `europe-west2`).
8. **Push delivery pulled forward from R2 to ship alongside R1 alert generation**: with Firebase/FCM credentials provisioned (#108, PRs #173/#176/#177) and statistical alerts already producing real `Alert` rows (PRs #116/#118), the provisioning lead time that motivated an early start outweighed staying strictly wave-ordered. Quiet-hours/lock-screen preferences (originally R2) shipped with it since they share the same `NotificationPreference` surface. Per-member preference scoping, notification sensitivity tuning, and the AI pipeline's own `long_term_trend` alerts remain R2.

## Cross-References

- Product roadmap narrative: [solution_manifest.md](./solution_manifest.md) (quarters map to waves above)
- Mobile screens: [ui_screens_maui_mobile.md](./execution/ui/mobile/ui_screens_maui_mobile.md) (MVP 1–3)
- Web screens: [ui_screens_blazor_web.md](./execution/ui/web/ui_screens_blazor_web.md) (MVP 1–4)
- API priorities: [execution/backend/api/readme.md](./execution/backend/api/readme.md)
- OAuth client inventory + restricted-scope verification detail: [technical/oauth_clients.md](./technical/oauth_clients.md)
- Google verification gates (Step 6): [technical/user_onboarding_process.md](./technical/user_onboarding_process.md)
- Privacy/security posture: [compliance/dpia.md](./compliance/dpia.md), [technical/data_protection_architecture.md](./technical/data_protection_architecture.md)

---

**Document Version:** 2.4
**Last Updated:** August 14, 2026
**Owner:** Product Lead
