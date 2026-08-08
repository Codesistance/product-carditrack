# CardiTrack documentation map

Every file in `docs/`, what's in it, and when a PM should open it. Paths are repo-relative; links resolve from this file.

## Precedence when docs disagree

Stated in [docs/readme.md](../../../../docs/readme.md) — apply it, don't average conflicting docs:

1. [release_matrix.md](../../../../docs/release_matrix.md) — release sequencing
2. [execution/backend/api/](../../../../docs/execution/backend/api/readme.md) — API contracts
3. [llm_design.md](../../../../docs/llm_design.md) — AI pipeline architecture
4. [infrastructure.md](../../../../docs/infrastructure.md) — infrastructure and the transactional data model
5. [solution_manifest.md](../../../../docs/solution_manifest.md) — business/product facts

Anything in [docs/archive/](../../../../docs/archive/) is **superseded and never canonical**.

## Doc conventions

- Filenames are `lowercase_snake_case.md`; `readme.md` indexes a directory.
- Major docs carry a table of contents, overview, detailed content, and cross-references — match that shape when adding one.
- Files under `docs/execution/ui/mobile/mvp1/` are **extracts, marked "do not edit directly"**. Edit the canonical parent and re-extract (`convert_to_pdf.py` regenerates the PDFs).
- Ownership table (who reviews what) is in [docs/readme.md](../../../../docs/readme.md); the Product Lead owns `solution_manifest.md` and `release_matrix.md`.

---

## Product & business

| Doc | Contents | Open it when |
|---|---|---|
| [solution_manifest.md](../../../../docs/solution_manifest.md) | Executive summary, mission, differentiators, **pricing tiers & unit economics**, tech architecture, core features (multi-device, AI pattern analysis, preventive alerts, family dashboard, **regulatory posture**), data model, GTM phases, **KPIs**, risk factors (technical/business/regulatory), team, roadmap, success criteria | Any pricing, packaging, positioning, or metric question; checking a feature's stated intent; sanity-checking a roadmap claim |
| [release_matrix.md](../../../../docs/release_matrix.md) | **Canonical release plan.** Release waves R1–R4, feature × API/Mobile/Web × wave × plan gate × **build status**, resolved-conflicts decision log, cross-references | Every scoping or sequencing decision. Read this before saying anything ships in a given release, and before claiming a feature exists |
| [market_analysis.md](../../../../docs/market_analysis.md) | Market size & growth, drivers, trends, **three customer segments with pains and JTBD**, direct + indirect competitor teardowns, feature comparison matrix, UVPs, positioning map, brand messaging, GTM phases, risks & opportunities | Writing the Problem statement; justifying Value risk; arguing differentiation; anything competitive |
| [google_credits_pitch.md](../../../../docs/google_credits_pitch.md) | Company overview, problem/solution narrative, GCP usage plan for the Google for Startups credits programme | Need a tight external-facing narrative or the current cost framing |
| [readme.md](../../../../docs/readme.md) | Documentation index, conventions, **precedence rules**, ownership table, version history, external resources | Adding or reorganising a doc; unsure which doc wins |

## Specs — canonical contracts

### API — source of truth for all `/api/v1/*`

[execution/backend/api/readme.md](../../../../docs/execution/backend/api/readme.md) is the index; endpoint priorities P0–P2 are **relative to the wave the feature ships in** per the release matrix. App READMEs link here and never duplicate endpoints.

- [auth.md](../../../../docs/execution/backend/api/auth.md) — Auth0 Universal Login, tokens, email verification. No local password endpoints
- [cardimembers.md](../../../../docs/execution/backend/api/cardimembers.md) — CardiMember CRUD, profile, medical notes, emergency contacts
- [devices.md](../../../../docs/execution/backend/api/devices.md) — connection, status, primary device, reconnect, remove
- [health-data.md](../../../../docs/execution/backend/api/health-data.md) — metrics, dashboard, baselines, exports
- [alerts.md](../../../../docs/execution/backend/api/alerts.md) — alert types, severity, acknowledgment, notes
- [family.md](../../../../docs/execution/backend/api/family.md) — invitations, roles (admin/staff/viewer), shared notes
- [notifications.md](../../../../docs/execution/backend/api/notifications.md) — channels, preferences, quiet hours, push registration
- [subscriptions.md](../../../../docs/execution/backend/api/subscriptions.md) — **canonical plan limits and gates**, trial handling
- [reports.md](../../../../docs/execution/backend/api/reports.md) — report generation and formats

**Open these before writing acceptance criteria that touch data** — reuse the real field names, error codes, and limits instead of inventing them.

### UI

| Doc | Contents | Open it when |
|---|---|---|
| [execution/ui/mobile/ui_screens_maui_mobile.md](../../../../docs/execution/ui/mobile/ui_screens_maui_mobile.md) | **Canonical mobile screen spec** (MVP 1–3): every screen, its states/variations, build status, navigation graph, and the "Shipped Screens Without Figma M1 Frames" section | Any mobile UI change. Edit here, not in the mvp1 extract |
| [execution/ui/mobile/user_stories.md](../../../../docs/execution/ui/mobile/user_stories.md) | **Canonical mobile user stories** with priorities and screen mappings | Adding or revising a mobile story |
| [execution/ui/mobile/mvp1/screens.md](../../../../docs/execution/ui/mobile/mvp1/screens.md) · [mvp1/user_stories.md](../../../../docs/execution/ui/mobile/mvp1/user_stories.md) | MVP 1 extracts (+ generated PDFs, `convert_to_pdf.py`) | Reading MVP 1 quickly, or sharing a PDF. **Do not edit — extracts** |
| [execution/ui/web/ui_screens_blazor_web.md](../../../../docs/execution/ui/web/ui_screens_blazor_web.md) | Canonical web screen spec (MVP 1–4) | Any web UI change. Note the web app is still template-stage |
| [execution/ui/web/user_stories.md](../../../../docs/execution/ui/web/user_stories.md) | Canonical web user stories | Adding or revising a web story |

## Architecture & platform

| Doc | Contents | Open it when |
|---|---|---|
| [llm_design.md](../../../../docs/llm_design.md) | AI pipeline on GCP: Pub/Sub + Cloud Run, MedGemma via Ollama, Gemini 2.0 Flash, webhook aggregation, SSA-LSTM pre-processing, prompt structure, **severity routing**, predictive monitoring, digests, cost estimates, caveats | Anything AI-touching: insights, chat, predictions, `long_term_trend` alerts, digests, AI cost |
| [infrastructure.md](../../../../docs/infrastructure.md) | Cloud SQL PostgreSQL 16 as system of record, schema & entity relationships, EF Core + migrator Cloud Run Job, AES-256-GCM + Secret Manager, GCP resources, Terraform stacks, CI/CD, monitoring, scaling, DR | Feasibility on storage, retention, scale, cost, or deployment |
| [apps/api/readme.md](../../../../docs/apps/api/readme.md) | ASP.NET Core Web API — stack, structure, middleware, config, local run (Swagger non-prod only) | Orienting on the API app itself |
| [apps/web/readme.md](../../../../docs/apps/web/readme.md) | Blazor Web App — current template-shell state, disclosure banner, privacy page, APM/DataProtection wiring, planned dashboard | Any web scoping — **read this before assuming web parity with mobile** |
| [apps/mobile/readme.md](../../../../docs/apps/mobile/readme.md) | .NET MAUI app — iOS/Android architecture, Mobile.Core, Auth0 native login, onboarding flow, APM/crash reporting, planned HealthKit/Health Connect/push/offline. States that **UI scope is governed by the Figma M1 file** | Any mobile scoping |
| [apps/mobile/store_provisioning.md](../../../../docs/apps/mobile/store_provisioning.md) | Keys, certs, and Secret Manager secrets for TestFlight + Play internal testing delivery | Release/launch planning for mobile |
| [apps/worker/readme.md](../../../../docs/apps/worker/readme.md) | `CardiTrack.Worker` — the only home for non-AI background jobs (30-min wearable sync with in-path token refresh, daily orphaned-org cleanup), Cronos scheduling | Any feature needing a scheduled/background job |

## Technical reference

| Doc | Open it when |
|---|---|
| [technical/auth0_integration.md](../../../../docs/technical/auth0_integration.md) | Auth flows, OAuth, security config — any sign-in, session, or identity story |
| [technical/auth0_setup_runbook.md](../../../../docs/technical/auth0_setup_runbook.md) | Per-environment tenant config — release/launch checklists |
| [technical/oauth_clients.md](../../../../docs/technical/oauth_clients.md) | Every OAuth client (identity vs device-data), social log-on scope, **restricted-scope verification detail** — device connection, social sign-in, and the 100-wearer cap |
| [technical/user_onboarding_process.md](../../../../docs/technical/user_onboarding_process.md) | Onboarding + device-connection flow step by step, incl. **Step 6 Google verification gates** — any onboarding or activation work |
| [technical/entity_summary.md](../../../../docs/technical/entity_summary.md) | Domain entities, properties, relationships — get names right in specs |
| [technical/data_protection_architecture.md](../../../../docs/technical/data_protection_architecture.md) | HIPAA/GDPR ADR: identifier/clinical schema separation, Safe Harbor de-identification, retention & erasure jobs, audit/consent models, subprocessor register — **any new health data field, sharing surface, or export** |
| [technical/apm_setup_runbook.md](../../../../docs/technical/apm_setup_runbook.md) | Serilog + OpenTelemetry, switchable `Apm:Engine` (Datadog) — **how a success metric will actually be measured**; if you can't name the signal, it isn't a metric yet |
| [technical/enum_extensions_guide.md](../../../../docs/technical/enum_extensions_guide.md) | Enum conventions — when a spec introduces a new status/type value |

## Compliance

| Doc | Open it when |
|---|---|
| [compliance/dpia.md](../../../../docs/compliance/dpia.md) | GDPR Art. 35 DPIA — processing inventory, risk assessment, mitigations. **Any feature that changes what data is processed, why, or who sees it needs a DPIA line.** Flag it as a Viability item and route it to the compliance owner |

## Root

[CLAUDE.md](../../../../CLAUDE.md) — binding architecture and UI rules (Worker-only background jobs, GCP-only AI pipeline, full-bleed pages). These override defaults; a spec that violates them will be rejected in review.
