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

Legend: wave number = ships in that wave; — = not planned for that surface. **Status** is as of **August 9, 2026**: ✅ Shipped · 🔶 In progress / partial · ⬜ Not started.

| Feature | API | Mobile | Web | Plan gate | Status (Aug 9, 2026) |
|---------|-----|--------|-----|-----------|----------------------|
| Auth0 Universal Login (email + password) | R1 | R1 | R1 | — | ✅ Shipped, incl. email-verification gate |
| Social sign-in (Google / Apple via Auth0) | R1 | R1 | R1 | — | 🔶 Buttons shipped but unwired (Phase 9; social connection credentials pending) |
| Onboarding (atomic account + org + CardiMember setup) | R1 | R1 | R1 | — | ✅ Shipped, incl. org-orphaning cleanup fix (PR #5) |
| CardiMember CRUD + profile | R1 | R1 | R1 | Member limit by tier | 🔶 API + mobile shipped (GET/PUT/DELETE `/api/v1/cardimembers/{id}` + detail/edit screens); web not started (template-stage). Tier member limit not yet enforced |
| Emergency contacts, medical notes (encrypted) | R1 | R1 | R1 | — | 🔶 API + mobile shipped inside member CRUD (medical notes AES-encrypted at rest); web not started (template-stage) |
| Consent recording (per-metric) | R1 | R1 | R1 | — | ⬜ Not started |
| Fitbit connection (Google Health API — server OAuth + REST client) | R1 | R1 | R1 | **100 connected wearers max until Google verification passes** | ✅ Client shipped (PR #10, migrated off legacy Fitbit Web API); 🔶 Google console registration + sandbox verification of "(assumed)" response fields pending |
| Fitbit webhook subscriptions (push ingestion) | R2 | R2 | R2 | — | ⬜ Not started — moves to R2 with the AI pipeline (GCP Pub/Sub + Cloud Run); R1 ingestion is 10-minute Worker polling (✅ shipped) |
| Device management (status, primary, reconnect, remove) | R1 | R1 | R1 | — | 🔶 API + mobile shipped (remove / set-primary / sync / refresh endpoints + device management screen, M1-15); web not started (template-stage) |
| Dashboard + daily health summary | R1 | R1 | R1 | — | 🔶 Per-member dashboard endpoint + mobile dashboard shipped; web dashboard not started (web app is still template-stage) |
| Statistical alerts (all 5 launch types) + acknowledgment/notes | R1 | R1 | R1 | — | ⬜ Not started (no alerts CRUD/acknowledgment; no SMS/email/push delivery channels built) |
| AI insights + chat endpoints (MedGemma via Ollama on Cloud Run; Gemini 2.0 Flash) | R1 | R1 | R1 | — | ✅ Shipped (synchronous endpoints; the R2 event-driven pipeline is separate) |
| Reports (health report generation) | R1 | R1 | R2 | Complete Care | 🔶 Text-only generation shipped; PDF/CSV/FHIR R4 formats not started |
| Push notification registration | R1 | R1 | — | — | ⬜ Not started |
| Health data export — PDF, CSV, FHIR R4 | R1 | R1 | R2 | Complete Care | ⬜ Not started (see reports row for text-only interim) |
| Baseline learning progress | R1 | R1 | R1 | — | 🔶 Daily `BaselineCalculationWorker` + mobile learning screen shipped; web not started (web app is still template-stage) |
| Monitoring pause / resume | R1 | R1 | R1 | — | 🔶 API + mobile shipped (pause/resume endpoints; paused members excluded from sync scheduling); web not started (template-stage) |
| **30-day trial (no billing UI)** | R1 | R1 | R1 | — | ✅ Shipped — trial provisions the **Complete Care tier** for 30 days |
| Region-localized phone input (UK groundwork) | R1 | R1 | R1 | — | ✅ Shipped (PR #8) |
| Health-data disclosure (Google-mandated in-app disclosure) | R1 | R1 | R1 | — | 🔶 Web shipped (PR #9); **mobile missing — gate for public launch** |
| Observability (Datadog APM, opt-in metrics via `Apm:Engine`) | R1 | — | — | — | ✅ Shipped (PR #4) |
| **Google restricted-scope verification + annual CASA** | R1→R2 gate | R1→R2 gate | R1→R2 gate | **Blocks >100 connected wearers** | ⬜ Not started — cross-wave external gate: Gate 1 Trust & Safety review + Gate 2 annual CASA ($500–$4,500, 2–6 weeks; combined runway 4–8 weeks). See [user_onboarding_process.md Step 6](./technical/user_onboarding_process.md) and [oauth_clients.md](./technical/oauth_clients.md) |
| **Legacy Fitbit Web API sunset — September 2026** | external deadline | external deadline | external deadline | — | 🔶 Hard external deadline (~4 weeks away). Code migrated to Google Health API (PR #10); console registration done 2026-08-07; field mappings verified against the v4 discovery document 2026-08-09 (two silent-zero defects found and fixed); **blocking task: live-wearer check that each type is actually populated** |
| Trend charts (7d/30d/90d/custom) | R2 | R2 | R2 | — | ⬜ Not started |
| Notification preferences (global + per-member, quiet hours, sensitivity) | R2 | R2 | R2 | — | ⬜ Not started |
| **Subscriptions & billing (Stripe)** | R2 | R2 | R2 | — | ⬜ Not started (no Stripe integration exists yet) |
| Garmin connection | R2 | R2 | R2 | — | ⬜ Not started |
| **AI pipeline** (Pub/Sub → SSA-LSTM → MedGemma on Cloud Run; `long_term_trend` alerts; digests) | R2 | R2 | R2 | Advanced alerts: Complete Care | ⬜ Not started (design: [llm_design.md](./llm_design.md); platform decision — see decision log #7) |
| Export — HL7 v2 | R2 | R2 | R2 | Complete Care | ⬜ Not started |
| Predictive monitoring (prediction cards, morning outlook) | R3 | R3 | R3 | Complete Care | ⬜ Not started |
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
6. **Polling vs webhooks**: Worker polling **shipped as the R1 ingestion path** (originally 30-minute; default cadence reduced to **10 minutes** on Aug 9, 2026 — migration `ReduceDefaultSyncFrequencyToTenMinutes`) and remains the system of record for ingestion until R2. Webhook push subscriptions move to **R2**, delivered with the AI pipeline. The original R1 row bundled "server OAuth, webhooks" — that bundling is superseded; the matrix now splits them.
7. **AI pipeline platform**: the pipeline runs on **GCP — Pub/Sub + Cloud Run, with MedGemma served via Ollama on Cloud Run and Gemini 2.0 Flash for chat/reports** — superseding the earlier Azure Functions / Event Hubs design. This matches the deployed Terraform footprint (Cloud Run, Cloud SQL PostgreSQL, Secret Manager, `europe-west2`).

## Cross-References

- Product roadmap narrative: [solution_manifest.md](./solution_manifest.md) (quarters map to waves above)
- Mobile screens: [ui_screens_maui_mobile.md](./execution/ui/mobile/ui_screens_maui_mobile.md) (MVP 1–3)
- Web screens: [ui_screens_blazor_web.md](./execution/ui/web/ui_screens_blazor_web.md) (MVP 1–4)
- API priorities: [execution/backend/api/readme.md](./execution/backend/api/readme.md)
- OAuth client inventory + restricted-scope verification detail: [technical/oauth_clients.md](./technical/oauth_clients.md)
- Google verification gates (Step 6): [technical/user_onboarding_process.md](./technical/user_onboarding_process.md)
- Privacy/security posture: [compliance/dpia.md](./compliance/dpia.md), [technical/data_protection_architecture.md](./technical/data_protection_architecture.md)

---

**Document Version:** 2.1
**Last Updated:** August 9, 2026
**Owner:** Product Lead
