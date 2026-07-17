# CardiTrack Release Matrix

**The canonical release plan.** When any other document (manifest roadmap, UI screen specs, API priorities) disagrees with this matrix, this matrix wins. Platform docs keep their own MVP numbering (mobile has 3 MVPs, web has 4); this matrix maps them onto shared release waves.

## Release Waves

| Wave | Roadmap window | Mobile release | Web release | Theme |
|------|---------------|----------------|-------------|-------|
| **R1 — Core Monitoring** | Q1 2026 (MVP dev + beta) | Mobile MVP 1 | Web MVP 1 | Sign up, add CardiMembers, connect Fitbit, dashboard, all alert types, acknowledgment |
| **R2 — Management & Billing** | Q2 2026 (public launch) | Mobile MVP 2 (part) | Web MVP 2 | Trend charts, notification preferences, **subscriptions/billing**, Garmin, expanded exports; **AI pipeline rollout** ([llm_design.md](./llm_design.md)) |
| **R3 — Family & Multi-Member** | Q3 2026 | Mobile MVP 2 (part) | Web MVP 3 | Family invitations, shared notes, multi-member management, test-result scanning |
| **R4 — Native & Offline** | Q4 2026 | Mobile MVP 3 | Web MVP 4 | Biometric login, offline support, push actions, widgets/PWA, SNOMED CT export |

API endpoint priorities (P0–P2 in [/execution/backend/api/](./execution/backend/api/readme.md)) are **relative to the wave in which the feature ships**, per the matrix below.

## Feature Matrix

Legend: wave number = ships in that wave; — = not planned for that surface.

| Feature | API | Mobile | Web | Plan gate |
|---------|-----|--------|-----|-----------|
| Auth0 Universal Login (email/social) | R1 | R1 | R1 | — |
| CardiMember CRUD + profile | R1 | R1 | R1 | Member limit by tier |
| Emergency contacts, medical notes (encrypted) | R1 | R1 | R1 | — |
| Consent recording (per-metric) | R1 | R1 | R1 | — |
| Fitbit connection (server OAuth, webhooks) | R1 | R1 | R1 | — |
| Device management (status, primary, reconnect, remove) | R1 | R1 | R1 | — |
| Dashboard + daily health summary | R1 | R1 | R1 | — |
| Statistical alerts (all 5 launch types) + acknowledgment/notes | R1 | R1 | R1 | — |
| Push notification registration | R1 | R1 | — | — |
| Health data export — PDF, CSV, FHIR R4 | R1 | R1 | R2 | Complete Care |
| Baseline learning progress | R1 | R1 | R1 | — |
| Monitoring pause / resume | R1 | R1 | R1 | — |
| **30-day trial (no billing UI)** | R1 | R1 | R1 | — |
| Trend charts (7d/30d/90d/custom) | R2 | R2 | R2 | — |
| Notification preferences (global + per-member, quiet hours, sensitivity) | R2 | R2 | R2 | — |
| **Subscriptions & billing (Stripe)** | R2 | R2 | R2 | — |
| Garmin connection | R2 | R2 | R2 | — |
| **AI pipeline** (Event Hubs → SSA-LSTM → MedGemma; `long_term_trend` alerts; digests) | R2 | R2 | R2 | Advanced alerts: Complete Care |
| Export — HL7 v2 | R2 | R2 | R2 | Complete Care |
| Predictive monitoring (prediction cards, morning outlook) | R3 | R3 | R3 | Complete Care |
| Family invitations + roles (admin/staff/viewer) | R3 | R3 | R3 | Family-member limit by tier |
| Shared care notes + @mentions | R3 | R3 | R3 | — |
| Multi-member comparison views | R3 | R3 | R3 | — |
| Test-result scanning + insights | R3 | R3 | R3 | Complete Care |
| Export — LOINC/CCD | R3 | R3 | R3 | Complete Care |
| Activity/audit log endpoint (Admin) | R3 | R3 | R3 | — |
| Biometric login (local gate) | R4 | R4 | — | — |
| Offline support + sync queue | R4 | R4 | R4 (PWA) | — |
| Push notification inline actions | R4 | R4 | R4 (browser) | — |
| Home-screen widget / PWA install | R4 | R4 | R4 | — |
| Apple Watch (on-device HealthKit bridge) | R3 | R3 | n/a | — |
| Samsung Health connection | R3 | R3 | R3 | — |
| Withings / Oura / Whoop | R4 | R4 | R4 | — |
| Export — SNOMED CT | R4 | R4 | R4 | Complete Care |
| Enterprise / Guardian Plus (business flow) | post-R4 | post-R4 | post-R4 | Guardian Plus |

## Resolved Conflicts (decision log)

1. **Subscriptions**: endpoints and UI ship together in **R2** — R1 is trial-only, and R2 lands before the first R1 trials expire. (Previously: API P0 "launch blocker" vs UI specs MVP 2.)
2. **FHIR R4 export**: mobile ships it in R1, web in R2 — **deliberate**: mobile is the primary caregiver surface for doctor-visit prep.
3. **Export plan-gating**: all export formats require **Complete Care** (Basic has no export), consistent with [subscriptions.md](./execution/backend/api/subscriptions.md).
4. **`long_term_trend` alerts** require the AI pipeline and therefore ship in R2, not R1 — the five R1 alert types are statistical.
5. **AI severity taxonomy**: internal Critical/High/Medium/Low maps to user-facing red/orange/yellow/green everywhere ([llm_design.md](./llm_design.md)).

## Cross-References

- Product roadmap narrative: [solution_manifest.md](./solution_manifest.md) (quarters map to waves above)
- Mobile screens: [ui_screens_maui_mobile.md](./execution/ui/mobile/ui_screens_maui_mobile.md) (MVP 1–3)
- Web screens: [ui_screens_blazor_web.md](./execution/ui/web/ui_screens_blazor_web.md) (MVP 1–4)
- API priorities: [execution/backend/api/readme.md](./execution/backend/api/readme.md)

---

**Document Version:** 1.0
**Last Updated:** July 17, 2026
**Owner:** Product Lead
