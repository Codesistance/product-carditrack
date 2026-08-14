# CardiTrack product context

Facts a PM needs before writing anything. **Sourced from `docs/` as of August 14, 2026 — re-read the source doc before quoting a number in an artefact.** Where this file and a `docs/` file disagree, the `docs/` file wins.

## What the product is

Remote health monitoring for elderly family members. A caregiver connects their relative's wearable (Fitbit via Google Health API today; Garmin/Samsung/Apple Watch later), CardiTrack learns that person's baseline, and alerts the caregiver when the data deviates — before it becomes an emergency.

- The person being monitored is a **CardiMember**. They often have no login and may never open the app.
- The person paying and receiving alerts is the **caregiver** — a family member of an organisation account.
- Positioning: preventive, non-stigmatising, and **not a medical device** — no diagnosis, no treatment advice.

## Who it's for

| Segment | Who | What they hire it for |
|---|---|---|
| **Primary — family caregivers** | 45–65, ~65% female, $50–150k household, suburban/urban, tech-comfortable | Monitor a parent remotely, get early warning of decline, cut anxiety, coordinate with siblings, avoid ER visits |
| **Secondary — independent elderly** | 70–85, living independently, already own a wearable | Age in place, not burden their kids, know before it's serious |
| **Tertiary — assisted living facilities** | Care homes | Post-MVP, served by the Guardian Plus business flow |

Caregiver pain, in their words: *"I worry about my mom living alone." "Medical alert systems are too expensive." "My dad won't wear a medical device — too stigmatising." "Checking in daily feels intrusive."*

Design consequence: the buyer is anxious and time-poor; the wearer resists anything that signals frailty. Features that make the wearer feel surveilled are a churn risk on the *secondary* segment even when they delight the primary one. Say so when it applies.

Source: [market_analysis.md § Target Customer Segments](../../../../docs/market_analysis.md)

## Pricing & plan gates

All consumer plans start with a **30-day free trial** (the trial provisions Complete Care).

| | **Basic** $8/mo ($81.60/yr) | **Complete Care** $15/mo ($153/yr) | **Guardian Plus** $29.99/mo *(post-MVP, business)* |
|---|---|---|---|
| CardiMembers | 2 | 5 | Unlimited |
| Family members | 5 | 20 | Unlimited |
| Data history | 90 days | 365 days | 2 years |
| Alerts | Email, standard types | Real-time SMS/email/push, advanced AI types | + 24/7 monitoring dashboard |
| Export | None | PDF, CSV, FHIR R4 | + API access |
| Devices | BYO wearable | Any supported device, multi-device per member | + telemedicine integration |

Add-ons: Fitbit Charge 6 bundle +$100 upfront (margin-neutral); annual billing −15%.

Unit economics (Complete Care): ~$15 revenue − ~$2 cost = **$13/user/month margin**. LTV >$300 requires churn below ~4%/month. Plan limits are canonical in [subscriptions.md](../../../../docs/execution/backend/api/subscriptions.md); prices in [solution_manifest.md § Business Model](../../../../docs/solution_manifest.md).

**Rule:** every export format is Complete Care — Basic has no export. Don't propose an export feature for Basic.

## Target metrics already committed

Use these as baselines before inventing new ones. Source: [solution_manifest.md § Key Metrics & KPIs](../../../../docs/solution_manifest.md).

- **Product:** false-positive rate <10% at MVP / <5% steady state · alert delivery latency <30s · data-sync success >99% · token-refresh success >99.5%
- **Engagement:** DAU (family members) · alert acknowledgment rate · time-to-acknowledge · dashboard session duration
- **Acquisition:** CAC <$50 · trial→paid conversion >20%
- **Retention:** monthly churn <5% · LTV >$300 · NPS >50
- **Revenue:** MRR · ARPU $15–20 · LTV/CAC >3:1

If a PRD's success metric doesn't ladder up to one of these, justify it or replace it.

## Release waves

Canonical, and it wins over every other doc on sequencing: [release_matrix.md](../../../../docs/release_matrix.md).

| Wave | Window | Theme |
|---|---|---|
| **R1 — Core Monitoring** | Q4 2026 (MVP + beta) | Sign up, add CardiMembers, connect Fitbit, dashboard, all 5 statistical alert types, acknowledgment. **Trial only — no billing.** |
| **R2 — Management & Billing** | Q1 2027 (public launch) | Trend charts, notification preferences, **Stripe billing**, Garmin, expanded exports, **AI pipeline rollout** on GCP |
| **R3 — Family & Multi-Member** | Q2 2027 | Family invitations + roles, shared notes, multi-member views, test-result scanning, predictive monitoring |
| **R4 — Native & Offline** | Q3 2027 | Biometric login, offline sync, push actions, widgets/PWA, SNOMED CT export |

Platform docs keep their own MVP numbering (mobile 3 MVPs, web 4); the matrix maps them onto these waves.

**Resolved conflicts you must not re-open without new evidence** (decision log in the matrix): subscriptions ship API+UI together in R2 · FHIR export is mobile-R1 / web-R2 deliberately · all exports gated to Complete Care · `long_term_trend` alerts need the AI pipeline so they're R2 · severity taxonomy Critical/High/Medium/Low → red/orange/yellow/green · 10-minute Worker polling is the shipped R1 ingestion path (originally 30-minute); Google Health webhooks + the AI pipeline shipped in **dev** ahead of R2, prod remains gated · the AI pipeline runs on **GCP**, superseding the earlier Azure design · LSTM dropped 2026-08-10 (SSA + Math.NET remains).

## Build status — what actually exists

As of the matrix's August 14, 2026 snapshot. **Check the matrix, not this summary, before writing "we have".**

**✅ Shipped:** Auth0 Universal Login incl. email-verification gate · social sign-in on **mobile** (Auth0 PKCE via system browser; production tenant connections remain an ops gate) · onboarding (atomic account + org + CardiMember, sex captured) · Google Health API client · 10-minute Worker polling + webhook path in **dev** · per-member dashboard + mobile dashboard (unresolved-alerts strip, weather popup, delayed "Loading", 30-day baseline gate) · 16 of 17 Figma M1 screens (`AlertDetailPage` covers M1-11/12/16; only M1-17 export is unbuilt) · CardiMember CRUD, pause/resume, device management, emergency contacts, encrypted medical notes (mobile + API) · statistical + inactivity alerts with ack/undo and push · family questionnaires (permanence, gap-backed asking, in-card delete) · in-app data-completeness nudges + Safety-class push (auth-broken, three-tier battery) · FCM/APNs push spine · synchronous AI insights/chat + digest/assessor pipeline in **dev** · daily baseline worker (mean/σ live; median/MAD persisted unused) · Math.NET SSA eigen engine · 30-day trial · Datadog APM.

**🔶 Partial:** web app still template-stage (no Auth0, no dashboard) · reports text-only (no PDF/CSV/FHIR) · health-data disclosure shipped on web, **missing on mobile — public-launch gate** · alert notes/photos not started · per-member notification preferences and sensitivity unused · wearable battery code shipped, **blocked on the console `settings.readonly` scope** · enricher built but unprovisioned.

**⬜ Not started:** per-metric consent recording · Stripe billing · family invitations · M1-17 export · everything else R2+ that the matrix still marks ⬜.

The load-bearing remaining R1 gaps are **mobile health-data disclosure**, **M1-17 export**, **alert notes**, and **Google restricted-scope verification** (100-wearer cap). The alerting loop itself exists.

## Architecture constraints that bind product decisions

From [CLAUDE.md](../../../../CLAUDE.md), [infrastructure.md](../../../../docs/infrastructure.md), [llm_design.md](../../../../docs/llm_design.md):

- **Stack:** .NET 10 — `CardiTrack.API` (ASP.NET Core), `CardiTrack.Web` (Blazor), `CardiTrack.Mobile` (.NET MAUI, iOS 17+ / Android 12 API 31+), `CardiTrack.Worker`. GCP: Cloud Run, Cloud SQL PostgreSQL **16** (local/devcontainer/tests use Postgres **17**), GCS, Pub/Sub, Secret Manager, `europe-west2`. Terraform (common/dev/prod).
- **Non-AI background jobs and all DB polling live only in `CardiTrack.Worker`.** OAuth token refresh, baseline recalculation, trial reminders, retention/cleanup. No other project may host them. A feature needing a scheduled job is a Worker change — say so in feasibility.
- **The AI ingestion/inference pipeline runs on GCP (Pub/Sub + Cloud Run)** per `llm_design.md` — the only sanctioned exception, and it must not host non-AI jobs.
- **UI:** all pages are full-bleed, edge-to-edge; corner radius belongs on components, never the page shell. Specs proposing rounded page-level cards contradict house rules.
- **Data protection:** identifier and clinical data live in separated schemas with a Safe Harbor de-identification pipeline, retention/erasure jobs, and audit/consent models. New health fields need a schema home and a consent story before they get a story. See [data_protection_architecture.md](../../../../docs/technical/data_protection_architecture.md) and [dpia.md](../../../../docs/compliance/dpia.md).

## External gates and deadlines

| Gate | Impact |
|---|---|
| **Google restricted-scope verification** (Trust & Safety review + annual CASA assessment, $500–$4,500, combined runway 4–8 weeks) | **Hard cap of 100 connected wearers** until it passes. Not started. Any reach estimate above 100 wearers is fiction. |
| **Legacy Fitbit Web API sunset — September 2026** | Code migrated; console registration + field verification done; **blocking task is the live-wearer check that each type is actually populated**. |
| **Mobile health-data disclosure** | Google-mandated in-app disclosure; missing on mobile; blocks public launch. |
| **Stripe billing** | Doesn't exist. R1 trial expiry has no defined billing path — an explicit R2 entry criterion. |

Detail: [oauth_clients.md](../../../../docs/technical/oauth_clients.md), [user_onboarding_process.md § Step 6](../../../../docs/technical/user_onboarding_process.md), [release_matrix.md](../../../../docs/release_matrix.md).

## Vocabulary — use these exact terms

**CardiMember** (the monitored person) · **family member** (a caregiver with a role: admin / staff / viewer, R3) · **organisation** (the account tenant) · **baseline** (the learned normal) · **alert** (statistical in R1; `long_term_trend` is AI, R2) · **release wave** R1–R4 · **plan gate** (Basic / Complete Care / Guardian Plus) · **M1-nn** (Figma mobile frame ID).

Entity names must match [entity_summary.md](../../../../docs/technical/entity_summary.md). Endpoint shapes must match [the API spec](../../../../docs/execution/backend/api/readme.md). Don't coin a new noun when one of these fits.
