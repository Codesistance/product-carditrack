# CardiTrack — Google for Startups Cloud Credits Application

## Company Overview

**CardiTrack** is an AI-powered health monitoring platform that helps family caregivers keep elderly loved ones safe at home — affordably. We connect to the wearable devices people already own and use machine learning to detect health decline *before* it becomes an emergency.

CardiTrack is **built on Google end to end**: wearable data via the **Google Health API** (we migrated off the legacy Fitbit Web API ahead of its September 2026 decommission), medical inference with **MedGemma** (Google's medical LLM, self-hosted via Ollama on **Cloud Run**), conversational insights and report generation with **Gemini 2.0 Flash**, sign-in via **Google OAuth**, and a Terraform-managed **Google Cloud** footprint — Cloud Run, Cloud SQL (PostgreSQL), Secret Manager, Cloud Storage — in `europe-west2`.

We are currently in active development, targeting a private beta in Q4 2026.

---

## The Problem

53 million Americans are caring for an elderly family member. They worry constantly — and for good reason. Falls, cardiac events, and gradual cognitive decline often go undetected until a 911 call is made.

Traditional medical alert systems charge $47–68/month for reactive, one-size-fits-all hardware. Most elderly people refuse to wear them. Meanwhile, 27 million+ seniors in the US already own a Fitbit, Apple Watch, or Garmin — and no one is using that data intelligently.

---

## Our Solution

CardiTrack monitors the wearable data elderly users generate every day and applies personalized AI baselines to detect anomalies — unusual inactivity, elevated resting heart rate, disrupted sleep patterns, gradual mobility decline — and surfaces them to the family caregiver's dashboard.

**What is built today (August 2026):**
- **Fitbit + Pixel Watch integration via the Google Health API** — server-side OAuth and REST client shipped, migrated ahead of the September 2026 legacy-API sunset; console registration and field-mapping verification against the v4 discovery document are done
- **Background ingestion** — notify-then-fetch: Google Health webhooks trigger targeted syncs, with the worker's 10-minute poll as the guaranteed fallback; minute-grain series stored with multi-horizon rollups
- **AI insights and chat** — MedGemma (Ollama on Cloud Run, IAM-authorised via Google-signed OIDC tokens — health data never leaves Google Cloud) and Gemini 2.0 Flash for caregiver Q&A and plain-text health reports
- **Mobile app (.NET MAUI, iOS + Android)** — onboarding and per-member health dashboard live
- **Google-mandated health-data disclosure** — already shipped on the web app
- 30-day free trial (provisions the Complete Care tier), email-verification gate, opt-in observability

**Also built (ahead of the original roadmap):**
- Five alert types (activity decline, heart rate elevation, sleep disruption, no morning activity, long-term trend) — **implemented** as statistical rules, with **push delivery (FCM) shipped** including quiet hours and escalation; SMS/email are out of scope
- Event-driven AI pipeline on **Pub/Sub + Cloud Run** — **live in dev**: SSA pre-processing over per-user baselines feeding MedGemma severity routing, plus rolling family summaries; targeting <5% false positive rate vs. 20–30% industry standard

**On the roadmap (see table below):**
- Additional wearable brands: Garmin (Q1 2027), Apple Watch and Samsung (Q2 2027), Withings/Oura/Whoop (Q3 2027)
- Real-time alerting to the web dashboard — planned (mobile push is live)
- FHIR R4 / HL7 v2 data exports — planned (data contracts defined)
- Blazor Server web dashboard — in early development; mobile is the primary surface today

---

## Target Market

| Segment | Size |
|---|---|
| US family caregivers | 53 million |
| US adults 65+ | 59 million |
| EU adults 65+ | 90 million |
| Seniors who already own compatible wearables | 27 million+ (US) |

**TAM**: $9B+ globally. Market growing at 19.5% CAGR (2024–2030).

---

## Business Model

| Plan | Price | Limits | Target |
|---|---|---|---|
| Basic | $7/month | 1 CardiMember, 5 family members, 30-day history, no export | Budget-conscious families |
| Complete Care | $10/month | 3 CardiMembers, 20 family members, 90-day history, data export | Core offering |
| Guardian Plus | $15/month | 6 CardiMembers, 180-day history, monthly Daybook, priority support | Larger households |
| Enterprise | $5–10/resident/month | Custom | Assisted living facilities |

Annual billing carries a 15% discount. The 30-day free trial provisions the Complete Care tier.

**Unit economics (Complete Care, $15/month):** ~$13/month gross profit per subscriber after cloud costs. Assuming ~24-month average retention, that is ~$312 lifetime gross profit — consistent with the LTV >$300 target in our market analysis. CAC target: <$50. Recurring compliance cost: the annual Google CASA security assessment for restricted health scopes ($500–$4,500/year) is budgeted as a fixed operating cost.

---

## Why We Need Cloud Credits

CardiTrack is cloud-native and infrastructure-as-code from day one (Terraform, Docker, GitHub Actions CI/CD), running entirely on Google Cloud in `europe-west2`. Our current and near-term workloads:

- **Cloud Run services** — API, web app, webhook receiver, and self-hosted MedGemma inference (Ollama, IAM-authorised)
- **Background worker** — webhook-triggered targeted syncs with a 10-minute Google Health API poll as fallback, plus alerting, baselines and notification dispatch (shipped)
- **Cloud SQL (PostgreSQL)** — encrypted health data store with field-level AES-256-GCM for OAuth tokens
- **Gemini 2.0 Flash** — caregiver chat and health report generation (shipped)
- **AI pipeline** — Pub/Sub event ingestion + Cloud Run jobs: SSA pre-processing, MedGemma anomaly scoring, severity routing, rolling family summaries (**live in dev**; credits fund the GPU/CPU inference headroom to bring it to production scale)
- **Secret Manager, Cloud Storage, CI/CD staging environments**

Google Cloud credits would allow us to:
1. Complete R1 (core monitoring) and run a private beta with 20–50 families
2. Fund GPU/CPU inference headroom as the Pub/Sub + Cloud Run AI pipeline comes online
3. Validate unit economics before committing to paid cloud spend at scale

---

## Traction & Roadmap

All dates below are the current re-baselined plan (as of August 7, 2026); nothing has publicly launched yet.

| Milestone | Timeline |
|---|---|
| **Google Health API client verification** — console registration done (Aug 7) and field mappings verified against the v4 discovery document (Aug 9); the live-wearer population check remains (legacy Fitbit Web API sunsets September 2026) | Aug–Sep 2026 |
| R1 — core monitoring complete: Fitbit via Google Health API, mobile dashboard, alerts | Q4 2026 |
| Private beta — 20–50 families (within the 100-user cap for unverified restricted-scope apps) | Q4 2026 |
| **Google restricted-scope verification submission** — Gate 1 Trust & Safety review + Gate 2 CASA security assessment (annual). The Google-required health-data disclosure already ships on our web app — we treat verification as diligence, not a checkbox | Q4 2026 |
| Public launch + subscriptions/billing + Garmin support + AI pipeline (Pub/Sub + Cloud Run) — requires verification passed to exceed 100 connected users | Q1 2027 |
| Apple Watch & Samsung support, family collaboration features | Q2 2027 |
| Offline support, expanded clinical exports; 1,000+ paying subscribers | Q3 2027 |
| Enterprise / assisted-living tier (Guardian Plus) | Q4 2027+ |
| UK, Canada, Australia expansion (groundwork begun: region-localized onboarding shipped Aug 2026) | 2027+ |

---

## Team

CardiTrack is being built by a founder with full-stack .NET development experience, designing the platform end-to-end — backend API, ML pipeline, Blazor web dashboard, MAUI mobile app, and cloud infrastructure.

---

## Summary

CardiTrack addresses a large, underserved market with a meaningfully differentiated product: 65–85% cheaper than incumbents, preventive rather than reactive, compatible with devices people already own, and powered by Google's own health AI stack — Google Health API in, MedGemma and Gemini inference in the middle, Google Cloud underneath. Cloud credits would directly accelerate our ability to get a working product into the hands of families who need it.

---

**Document Version:** 2.1
**Last Updated:** August 13, 2026
