# CardiTrack Solution Manifest

## Executive Summary

**CardiTrack** is a multi-device elderly health monitoring platform that provides affordable, preventive health monitoring for families using existing wearable devices with AI-powered pattern analysis.

**Core Value Proposition:** Family members get peace of mind and early warning of health issues at $7-15/month (vs $40-70/month for medical alert systems), using hardware their elderly parents likely already own.

**Target Market:** 150M+ adults aged 65+ across the US and EU, and their family caregivers — 53M in the US *(AARP/NAC, 2020)* and 44M in the EU *(Eurocarers)* — who bear primary responsibility for monitoring elderly relatives living independently.

---

## Product Vision

### Mission Statement

To empower families with affordable, preventive health monitoring for their elderly loved ones, enabling early detection of health concerns and peace of mind through intelligent pattern analysis.

### Core Differentiators

1. **Preventive vs Reactive**: Catches health issues BEFORE emergencies (not just fall detection)
2. **Affordable**: 65-85% cheaper than medical alert systems
3. **Non-Intrusive**: Uses existing devices, not new medical equipment
4. **AI-Powered**: Learns individual baselines, reduces false alerts
5. **Device-Agnostic**: Works with Fitbit, Apple Watch, Garmin, Samsung, and more
6. **No Hardware Lock-in**: Works with devices people already own

---

## Business Model

### Pricing Tiers

> Plan limits below are canonical and match the [Subscription API](./execution/backend/api/subscriptions.md). All consumer plans start with a **30-day free trial**.

> **Repriced 2026-08-18.** The ladder below replaces $8 / $15 / $29.99 with 2 / 5 / unlimited members and 90-day / 365-day / 2-year history. Each step now roughly doubles, and the Daybook cadence is the spine of the ladder. These figures match the published pricing page.

#### Tier 1: "Basic" - $7/month ($71.40/year)
- Bring your own wearable device
- Daily activity dashboard
- Daily health summary and digests
- **The Daybook — daily**
- **1 CardiMember**
- Up to **5 family members**
- Standard alert types
- **30-day** data history
- No data export

#### Tier 2: "Complete Care" - $10/month ($102/year)
- Support for any supported device
- Smart alerts when readings depart from baseline
- **The Daybook — daily, plus a weekly round-up**
- Advanced AI alert types (pattern analysis)
- Intraday heart rate, steps and sleep stages
- Up to **3 CardiMembers**
- Up to **20 family members**
- **90-day** data history
- Multi-device support per member
- PDF, CSV, and FHIR R4 data export

#### Tier 3: "Guardian Plus" - $15/month ($153/year)
- Everything in Complete Care, plus:
- **The Daybook — daily, weekly and monthly**
- Up to **6 CardiMembers**
- Priority support
- **180-day** data history

> Guardian Plus was previously specified as a post-MVP business tier at $29.99 and excluded from the consumer MVP. It is now a consumer tier and is sold as one on the pricing page. The assisted-living offering is separate — see the Enterprise line below and `enterprise.html`, which now recruits design partners rather than selling a product.

**Cadences that do not exist yet.** Only the **once-daily** Daybook is built (`DigestAudience.Daybook`, prod-gated). The **weekly** and **monthly** cadences above are sold on the pricing page and have no implementation — they are tracked in [release_matrix.md](./release_matrix.md).

**Sync cadence is not a tier lever.** Both environments deploy `min_pull_interval_minutes = 10`, so every tier polls on the same 10-minute cadence. What the paid tiers buy is minute-grain intraday *storage* and retention length, not a faster poll. Earlier copy claiming "high-resolution sync (1-min)" on Complete Care was withdrawn on 2026-08-18.

### Device Bundle Option (Add-on)
- **Fitbit Charge 6 Bundle**: +$100 upfront (includes device)
- **Annual Subscription**: 15% discount on all consumer tiers (Complete Care: $102/year, saves $18)

### Unit Economics (Tier 2 Example)

```
Monthly revenue per user: $10
Monthly costs per user: ~$2 (hosting, support — SMS is permanently out of scope)
Monthly margin per user: $8

Customer LTV (churn-derived, $8/month margin):
  At 5% monthly churn (launch target):  avg lifetime ~20 months → LTV ≈ $160
  At 3% monthly churn (growth target):  avg lifetime ~33 months → LTV ≈ $264
  A >$300 LTV now needs churn below ~2.7%/month, which is a materially
  harder target than the ~4% the old $15 price allowed. Re-derived after
  the 2026-08-18 repricing — the CAC assumptions elsewhere in this doc
  were set against the old margin and have NOT been revisited.

With Device Bundle:
  Hardware cost (bulk): ~$100, covered by the +$100 upfront bundle price
  → hardware is margin-neutral at purchase; subscription margin unchanged
```

---

## Technical Architecture

### Technology Stack

**Backend (transactional core):**
- .NET 10 (ASP.NET Core Web API)
- Entity Framework Core (Npgsql)
- Cloud SQL PostgreSQL 16 (system of record — identity, organizations, subscriptions, health data, audit)
- .NET Worker Service + Cronos (**non-AI background jobs only** — eleven cron jobs: wearable sync with in-path OAuth token refresh, orphan cleanup, baseline calculation, partition maintenance/retention, device-sync audit, inactivity detection, statistical alerting, device-auth recovery, data completeness, notification dispatch, push canary; see [apps/worker/readme.md](./apps/worker/readme.md))

**AI:**
- MedGemma 1.5 4B (`hf.co/unsloth/medgemma-1.5-4b-it-GGUF:Q4_K_M`) served via **Ollama on Cloud Run** — the Medical provider for health-data interpretation
- Gemini 2.0 Flash — the General provider for conversational responses
- Surfaced through the API's chat, insights, and reports endpoints
- Ingestion/inference pipeline on GCP (Pub/Sub + Cloud Run) — **live in dev** (webhooks registered, SSA → MedGemma assessment, digests, alert routing); see [llm_design.md](./llm_design.md) and the [C4 architecture](./architecture_c4.md)

**Frontend:**
- Blazor (web dashboard)
- .NET MAUI (cross-platform mobile app)
- Bootstrap 5 (UI framework)

**Infrastructure:**
- Google Cloud Platform (Cloud Run, Cloud SQL, Secret Manager, GCS, Pub/Sub in both environments, optional domain-gated Load Balancer/Cloud Armor)
- Terraform (Infrastructure as Code — common/dev/prod stacks, GCS backend)
- Docker (containerization)
- GitHub Actions (CI/CD)

**Observability:**
- Serilog + OpenTelemetry via a switchable APM engine (`Apm:Engine` — Datadog deployed; console-only Serilog when unset locally)

**External Integrations:**
- Google Health API (Fitbit, Pixel Watch, connected third-party sources — replaces the Fitbit Web API, which is decommissioned September 2026)
- Apple HealthKit *(planned)*
- Garmin Connect API *(planned)*
- Samsung Health SDK *(planned)*
- Withings API *(planned)*
- Auth0 (authentication)

### System Architecture

```
┌─────────────────────────────────────────────────────────────┐
│              FAMILY DASHBOARD (Web/Mobile)                  │
│           (Blazor Server / .NET MAUI)                       │
└─────────────────────────────────────────────────────────────┘
                            │
                            ↓
┌─────────────────────────────────────────────────────────────┐
│                    API GATEWAY                              │
│              (ASP.NET Core Web API)                         │
└─────────────────────────────────────────────────────────────┘
            │                    │                    │
            ↓                    ↓                    ↓
┌──────────────────┐  ┌──────────────────┐  ┌──────────────────┐
│  Device Services │  │  Alert Service   │  │  User Service    │
│  - Multi-device  │  │  - AI Analysis   │  │  - Auth          │
│  - Data Adapters │  │  - Notifications │  │  - Profiles      │
│  - OAuth Tokens  │  │  - Rules Engine  │  │  - Family Mgmt   │
└──────────────────┘  └──────────────────┘  └──────────────────┘
            │                    │                    │
            └────────────────────┴────────────────────┘
                            │
                            ↓
┌─────────────────────────────────────────────────────────────┐
│                   DATABASE LAYER                            │
│              (Cloud SQL PostgreSQL 16)                      │
│  - Organizations / Users / CardiMembers                     │
│  - Device Connections (Multi-device support)                │
│  - Activity Logs / Baselines / Alerts                       │
│  - Audit Logs (HIPAA compliance)                            │
└─────────────────────────────────────────────────────────────┐
                            │
                            ↓
┌─────────────────────────────────────────────────────────────┐
│         NON-AI BACKGROUND JOBS (CardiTrack.Worker)          │
│  - Eleven cron jobs: wearable sync (every 10 min, OAuth     │
│    refresh in the sync path), orphan cleanup, baselines,    │
│    partition retention, device-sync audit, inactivity +     │
│    statistical alerting, device-auth recovery, data         │
│    completeness, notification dispatch, push canary         │
│  - Full inventory: docs/apps/worker/readme.md               │
└─────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────┐
│   AI INGESTION & INFERENCE PIPELINE — BUILT, RUNNING IN DEV │
│                    (see llm_design.md)                      │
│                                                             │
│  Device webhooks → Pub/Sub → Cloud Run pipeline             │
│  (aggregation + pre-processing) → MedGemma (Ollama on       │
│  Cloud Run) → results store → Severity router → Alerts      │
└─────────────────────────────────────────────────────────────┘
```

> Webhook push ingestion is **live in dev** (receiver + Pub/Sub + Cloud Run pipeline; prod gated off). The Worker's **10-minute polling sync** (`WearableSyncWorker`) against the Google Health API remains the guaranteed fallback and runs in every environment. The Worker Service hosts only non-AI jobs.

### Multi-Device Architecture

CardiTrack uses the **Adapter Pattern** to support multiple wearable devices:

```
Device APIs (Fitbit, Apple, Garmin, Samsung, Withings, Oura, Whoop)
                    ↓
        Device-Specific Adapters
        (Normalize device data formats)
                    ↓
        Unified Health Data Model
        (Steps, Heart Rate, Sleep, SpO2, etc.)
                    ↓
        Pattern Analysis Engine
        (MedGemma via Ollama on Cloud Run)
                    ↓
        Contextual Family Alerts
        (Push (FCM/APNs) shipped; email not built)
```

---

## Core Features

### 1. Multi-Device Support

**Supported Devices (Roadmap):**

**Phase 1 (MVP - Months 1-3):**
- ✅ Fitbit (Charge 6, Inspire 3, Sense 2, Versa 4)

**Phase 2 (Months 3-6):**
- 🔄 Apple Watch (Series 8+, Ultra)
- 🔄 Garmin (Venu, Forerunner, Vivoactive)
- 🔄 Samsung Galaxy Watch (5, 6)

**Phase 3 (Months 6-12):**
- ⏳ Withings (ScanWatch, Body+)
- ⏳ Oura Ring (Gen 3)
- ⏳ Whoop (4.0)

**Device Capabilities Matrix:**

| Device          | Heart Rate | SpO2 | ECG | Steps | Sleep | GPS |
|-----------------|-----------|------|-----|-------|-------|-----|
| Fitbit Charge 6 | ✅        | ✅   | ✅  | ✅    | ✅    | ✅  |
| Apple Watch 9   | ✅        | ✅   | ✅  | ✅    | ✅    | ✅  |
| Garmin Venu     | ✅        | ✅   | ❌  | ✅    | ✅    | ✅  |
| Samsung Watch 6 | ✅        | ✅   | ✅  | ✅    | ✅    | ✅  |
| Withings Scan   | ✅        | ✅   | ✅  | ✅    | ✅    | ❌  |
| Oura Ring       | ✅        | ✅   | ❌  | ✅    | ✅    | ❌  |
| Whoop 4.0       | ✅        | ✅   | ❌  | ❌    | ✅    | ❌  |

### 2. AI-Powered Pattern Analysis

**Technology:** MedGemma 4B (medical LLM, served via Ollama on Cloud Run) with SSA pre-processing and deterministic trend features — see [llm_design.md](./llm_design.md) for the full pipeline design. The MVP launches with statistical threshold alerts; the MedGemma pipeline replaces them per the [release matrix](./release_matrix.md).

**Algorithms:**
- **Signal decomposition**: SSA (Singular Spectrum Analysis) — separates trend, oscillation, and noise per metric
- **Trend features**: deterministic slope/deviation features computed from the SSA output (no per-user models, no numeric risk scores)
- **Anomaly assessment**: MedGemma interprets the denoised trends and trend features qualitatively, assigns severity

**Learning Process:**
1. Collect baseline data per CardiMember — default 30 days (configurable up to 90)
2. Calculate personalized patterns (steps, heart rate, sleep)
3. Run daily anomaly detection comparing current vs baseline
4. Generate contextual alerts with severity levels
5. Continuously improve models with new data

**Personalized Baseline Metrics:**
- Average daily steps (with standard deviation)
- Resting heart rate patterns
- Sleep duration and quality
- Day-of-week activity patterns
- Typical sleep/wake times

### 3. Preventive Health Alerts

**Alert Types:**

#### Activity Alerts (Preventive)
**Example:**
```
Alert: "Unusual Inactivity"
Trigger: Steps < 50% of baseline for 2+ consecutive days
Severity: Yellow
Message: "Dad's activity has dropped 60% this week. Might be worth a call."
Prevention: Could indicate illness, injury, or depression BEFORE emergency
```

#### Heart Rate Alerts (Preventive)
```
Alert: "Elevated Resting Heart Rate"
Trigger: Resting HR >15% above baseline for 3+ days
Severity: Orange
Message: "Mom's resting heart rate has been elevated. Consider doctor visit."
Prevention: Could indicate infection, stress, or developing cardiac issue
```

#### Sleep Disruption Alerts (Preventive)
```
Alert: "Sleep Pattern Change"
Trigger: Sleep efficiency < 70% for 5+ days
Severity: Yellow
Message: "Dad's sleep quality has declined. Might indicate pain or anxiety."
Prevention: Sleep issues often precede other health problems
```

#### Sudden Pattern Break (Reactive)
```
Alert: "No Morning Activity"
Trigger: No movement detected by 11am (typical wake: 7am)
Severity: Red
Message: "Mom hasn't moved this morning. Please check on her."
Prevention: Fall, illness, or emergency detected early
```

#### Long-term Trend Alerts (Preventive)
```
Alert: "Declining Mobility Trend"
Trigger: Steps declining 5% per week for 4 consecutive weeks
Severity: Orange
Message: "Dad's activity trending down 20% this month. May need PT evaluation."
Prevention: Catches gradual decline before it becomes severe
```

**Alert severity taxonomy:** user-facing severities are **Green / Yellow / Orange / Red**. The AI pipeline's internal routing scale (Critical/High/Medium/Low) maps to them as Critical→Red, High→Orange, Medium→Yellow, Low→Green — see [llm_design.md](./llm_design.md).

**Target Metrics:**
- False positive rate: **<10% at MVP** (statistical alerts), **<5% steady-state** (MedGemma pipeline)
- Alert delivery latency (trigger → push received): <30 seconds
- Detection accuracy: >95%

### 4. Family Dashboard

**Web Dashboard (Blazor Server):**
- Real-time health metrics for all CardiMembers
- Activity, heart rate, and sleep trend charts
- Alert management (acknowledge, dismiss, add notes)
- Multi-member overview
- Weekly/monthly health reports
- Device connection management
- Family member access control

**Mobile App (.NET MAUI):**
- Cross-platform (iOS & Android)
- Push notifications for critical alerts
- Quick health overview
- Offline support with local SQLite cache *(planned — R4)*
- Platform-specific integrations (HealthKit on iOS) *(planned — R4)*

### 5. Regulatory posture and safeguards

> **Built to HIPAA technical safeguards. Not a covered entity or business associate today.**
> CardiTrack is a consumer wellness service governed by UK/EU GDPR and the FTC Health Breach
> Notification Rule. BAAs and attestation will be executed before any enterprise or clinical
> offering.

This section previously marked the whole of §164.312 and §164.308 as shipped. Most of it was
not, and unsubstantiated security claims are themselves the enforcement risk — they are the
FTC Act §5 theory used against GoodRx, Premom and BetterHelp. What follows is the verified
state. `docs/market_analysis.md` carries the same position in the competitor table.

**What actually governs us today**

| Regime | Applies | Why |
|---|---|---|
| UK/EU GDPR | ✅ Yes — primary | Health data is Art. 9 special category; UK/EU wearers from R1 |
| FTC Health Breach Notification Rule (16 CFR 318) | ✅ Yes | Covers direct-to-consumer health apps outside HIPAA |
| FTC Act §5 | ✅ Yes | Unfair/deceptive practices — this includes overstated security claims |
| State laws (e.g. WA My Health My Data) | ✅ Yes, on US launch | Private right of action |
| HIPAA | ⬜ Not yet | No covered entity in the chain: D2C sales, data arrives via the wearer's own Google consent, no function performed for a provider or plan |

HIPAA attaches at the first of: telemedicine integration (R4), any provider-facing data flow
(the FHIR R4/HL7 v2 export roadmap sits on this line), or enterprise/assisted-living sales —
facilities require a BAA in procurement regardless of whether the law compels one.

Dropping HIPAA as a *present-tense claim* is not the same as dropping it as a design target.
GDPR already requires audit logging (Art. 5(2), 32), encryption (32), least privilege (32) and
breach notification (33/34). The real delta is BAAs, six-year retention, policy and training
artifacts, and a §164.308-format risk analysis. Retrofitting is strictly worse: audit logging
added after real PHI exists leaves an unauditable gap that can never be closed.

**Technical safeguards (§164.312)**

| Control | State | Detail |
|---|---|---|
| Encryption at rest | ✅ | Cloud SQL, GCS — Google-managed keys |
| Encryption in transit | ✅ | TLS 1.2+ |
| Field-level encryption — OAuth tokens | ✅ | AES-256-GCM, key-id envelope for rotation |
| Field-level encryption — medical notes | ✅ | AES-256-GCM, encrypted at rest via `CardiMemberService` |
| Token policy | ✅ | Short-lived access tokens (15–60 min), rotating refresh tokens (30-day absolute), ~15-min web idle timeout, biometric re-auth on mobile open |
| Access controls — RBAC | 🔄 | `UserRole` exists and CardiMember access is gated per-caregiver; role enforcement is not yet applied across every endpoint |
| Access controls — MFA for admins | ⬜ | Auth0 tenant configuration, not yet enabled |
| Audit logging of PHI access | 🔄 | `AuditLoggingMiddleware` writes audit rows for the **eight** health-data controllers annotated `[AuditHealthDataAccess]` (CardiMembers, Dashboard, Devices, Insights, Chat, Reports, Questionnaires, Alerts); GET audits coalesce for 15 minutes. Onboarding's member creation remains unaudited |
| Least privilege | 🔄 | api, web, pipeline and webhook-receiver run as dedicated service accounts; worker, migrator and aggregator still share the default compute SA; applications connect to Postgres as admin (W1-6) |

**Administrative safeguards (§164.308) — not started**

Privacy policy, security policy, breach notification procedure, workforce training and a formal
risk analysis are all outstanding. None of these are code, and none should be claimed until the
document exists and someone owns it.

**Business Associate Agreements**

| Vendor | State |
|---|---|
| Google Cloud (Cloud SQL, GCS, Secret Manager, KMS, Cloud Run) | ⬜ Offered and free — not executed (tracked in issue #40) |
| Auth0 (Okta) | ⬜ Available on suitable plan tier — deferred to production go-live |
| Google Health API | n/a — no BAA offered; user-consent model under Google's Limited Use policy |
| Gemini consumer API | n/a — outside the Cloud BAA. Chat and reports send **de-identified daily readings** (no name/id on chat; reports label members positionally). Insights/digest/assess stay on in-project MedGemma. Moving Gemini to Vertex AI is decision D6 |

**Audit logging — implemented for annotated health-data controllers**

`AuditLoggingMiddleware` writes user ID, CardiMember ID, action, timestamp, IP address and user
agent, request-scoped so reads are captured and not just writes, for the eight health-data
controllers annotated `[AuditHealthDataAccess]` (CardiMembers, Dashboard, Devices, Insights, Chat,
Reports, Questionnaires, Alerts). Repeated GETs from the same caller coalesce into one row per
15-minute window. The remaining gap is Onboarding's member creation, which writes health data
without an audit row. Retention is 90 days for the platform
audit trail today (`enable_platform_audit_logging`); the six-year figure applies to HIPAA
§164.316(b)(2) documentation and PHI-access records, and becomes required only when HIPAA
attaches.

> Not legal advice. The covered-entity determination is a one-hour question for healthcare
> counsel and should be confirmed rather than inferred from this document.

---

## Data Model

### Core Entities

**Organization**
- Multi-tenant support
- Types: Family, Business
- Subscription management

**User**
- Family members/caregivers
- Roles: Member, Admin, Staff (Viewer maps to Member)
- Authentication via Auth0 Universal Login (JWT validation in the API; no local passwords)

**CardiMember**
- Elderly individuals being monitored
- Personal information
- Health baseline data
- Medical notes (encrypted)

**DeviceConnection**
- Multi-device support per CardiMember
- OAuth tokens (encrypted)
- Connection status tracking
- Primary device designation

**ActivityLog**
- Device-agnostic normalized health data
- Daily metrics: steps, heart rate, sleep, SpO2
- Links to source device
- Time-series data for analysis

**PatternBaseline**
- AI-learned normal patterns
- Personalized per CardiMember
- Recalculated daily at 02:30 UTC (new baselines appended, not replaced)
- Day-of-week variations

**Alert**
- Generated by pattern analysis
- Alert severity levels: Yellow (Caution), Orange (Urgent), Red (Critical); Green is a *health status*, not an alert severity — alerts exist only for non-green states
- Acknowledgment tracking
- Resolution workflow (`new → acknowledged → resolved`)

**AuditLog**
- Schema for access tracking — table, EF configuration, indexes and migration exist
- 🔄 Written request-scoped by `AuditLoggingMiddleware` for the eight `[AuditHealthDataAccess]`-annotated health-data controllers; GET audits coalesce for 15 minutes; Onboarding's member creation is the remaining unaudited path
- Retention target is set by the regime that applies — see §5

---

## Go-to-Market Strategy

### Phase 1: MVP Launch (Months 1-3)

**Month 1: Build MVP**
- Core .NET backend with Google Health API integration (Fitbit devices)
- Basic Blazor dashboard
- Simple alert rules (statistical, no ML yet)
- Database schema and migrations

**Month 2: Beta Testing**
- Recruit 10-20 families (friends, family, local community)
- Test with enrolled test users (Google Health API scopes are Restricted pre-verification)
- Collect feedback on alert accuracy
- Iterate on UX based on feedback

**Month 3: Apply for Device Approvals**
- Submit Google's restricted-scope privacy & security review (Google Health API production access)
- Apply for Apple HealthKit integration
- Refine baseline algorithms
- Prepare for launch

### Phase 2: Public Launch (Months 4-6)

- Launch with BYOD (Bring Your Own Device) pricing
- Content marketing: SEO, blog, YouTube tutorials
- Partnership outreach: senior centers, retirement communities
- Facebook/Google ads targeting caregivers (45-65 age group)

### Phase 3: Scale (Months 7-12)

- Add Apple Watch and Garmin support
- Launch device bundle option (subsidized hardware)
- Healthcare provider referral program
- Enterprise offering for assisted living facilities

### User Acquisition Channels

**Channel 1: Content Marketing**
- Blog: "How to monitor elderly parents remotely"
- SEO: Target "elderly health monitoring", "Fitbit for seniors"
- YouTube: Setup tutorials, testimonials
- Webinars: Caregiver education

**Channel 2: Senior Community Partnerships**
- Senior centers
- Retirement communities
- AARP partnerships
- Free trial for community members

**Channel 3: Healthcare Provider Referrals**
- Geriatric physicians
- Home health agencies
- Position as "peace of mind" tool (not medical device)

**Channel 4: Direct Advertising**
- Facebook ads: 45-65 year olds (caregiver demographic)
- Google Ads: "monitor elderly parents health", "aging parents safety"
- Retargeting campaigns

---

## Key Metrics & KPIs

### Product Metrics

**Health Metrics:**
- False positive rate: <10% at MVP, <5% steady-state target
- Alert delivery latency: <30 seconds
- Data sync success rate: >99%
- Token refresh success rate: >99.5%

**Engagement Metrics:**
- Daily active users (family members)
- Alert acknowledgment rate
- Time to acknowledge alert
- Dashboard session duration

### Business Metrics

**Acquisition:**
- Customer acquisition cost (CAC): <$50
- Conversion rate (trial → paid): >20%
- Channel performance tracking

**Retention:**
- Monthly churn rate: <5%
- Customer lifetime value (LTV): >$300
- Net Promoter Score (NPS): >50

**Revenue:**
- Monthly recurring revenue (MRR)
- Average revenue per user (ARPU): $15-20
- LTV/CAC ratio: >3:1

---

## Infrastructure & Operations

### Cloud Infrastructure (Google Cloud)

All environments run on GCP (project `carditrack-490120`, region `europe-west2`); exact sizing and costs live in [infrastructure.md](./infrastructure.md).

**Core shape (dev and prod):**
- **Cloud Run** services: `api`, `web`, `worker`, `medgemma` (Ollama, CPU), `webhook-receiver` (AI pipeline ingress, dev) + Cloud Run Jobs: a migrator for EF migrations and the pipeline's `digest`, `aggregator` and `assessor` jobs (dev) — scale-to-zero-friendly, pay-per-use
- **Cloud SQL PostgreSQL 16**: small shared-core instance in dev, `db-custom-2-7680` in prod
- **GCS** buckets (builds, data protection keys) and **Secret Manager** (all secrets)
- **Pub/Sub** (both environments; carries the AI pipeline's registered webhook traffic in dev)
- Optional domain-gated **Load Balancer + Cloud Armor** (not yet enabled in prod)

The Cloud Run pay-per-use model keeps pre-launch costs near zero and scales linearly with traffic; the dominant fixed costs are the prod Cloud SQL instance and the MedGemma Cloud Run service.

### Database Storage

**Per CardiMember Per Year:**
- Activity logs: 365 rows × ~500 bytes = 183 KB
- Pattern baselines: 12 rows × 2 KB = 24 KB
- Alerts: ~50 rows × 1 KB = 50 KB
- **Total**: ~260 KB/member/year

**10,000 CardiMembers:**
- Data: 2.6 GB/year
- With indexes: ~5 GB/year
- Cloud SQL PostgreSQL (`db-custom-2-7680`): Ample headroom

### Scaling Strategy

**Horizontal Scaling:**
- Cloud Run auto-scales instances on request concurrency and CPU
- Worker scales by Cloud Run instance count
- Max instances configurable per service via Terraform

**Database Scaling:**
- Read replicas for dashboard queries (when needed)
- Partition ActivityLogs by CardiMemberId
- Archive logs >2 years to cold storage (GCS)

**Caching:**
- In-memory cache for reference data; Memorystore for Redis backs the distributed cache (dev only today — see `enable_redis`)
- CDN for static assets

---

## Risk Factors & Mitigation

### Technical Risks

**Risk 1: Device API Changes**
- **Mitigation**: Abstract integrations behind interfaces
- **Backup**: Support multiple device types

**Risk 2: High False Positive Rate**
- **Mitigation**: 30-90 day personalized baselines
- **Backup**: User-configurable sensitivity settings

**Risk 3: OAuth Token Management**
- **Mitigation**: Proactive token refresh inside the 10-minute sync path
- **Monitoring**: Alert team if refresh rate drops

### Business Risks

**Risk 1: Market Rejection**
- **Mitigation**: Focus on "peace of mind" not "surveillance"
- **Validation**: Beta test with real families

**Risk 2: Competitor Launches Similar Feature**
- **Mitigation**: Move fast, build brand, capture market share
- **Strategy**: Position for acquisition by Google/Fitbit/Apple

**Risk 3: Regulatory Changes**
- **Mitigation**: Build to HIPAA technical safeguards from day 1 without claiming the status — see §5
- **Legal**: Regular healthcare attorney consultations; the covered-entity determination is the first question

### Regulatory / Legal Risks

**Risk 1: Data Breach**
- **Mitigation**: Multi-layer encryption, regular audits
- **Notification duty today**: FTC Health Breach Notification Rule and UK/EU GDPR Art. 33/34 — not HIPAA
- **Insurance**: Cyber liability ($1–2M)

**Risk 2: Unauthorized Access**
- **Mitigation**: Per-caregiver access checks on every health-data surface (shipped); RBAC, audit logging and MFA are ⬜ outstanding — see §5
- **Monitoring**: Real-time suspicious access alerts *(planned — depends on W1-2)*

**Risk 3: Overstated compliance claims**
- **Why it is listed**: this is the live exposure, not the gaps themselves. FTC Act §5 actions against GoodRx, Premom and BetterHelp turned on claims, not breaches
- **Mitigation**: §5 states verified status only; any ✅ must be traceable to code or an executed document

---

## Team Requirements

### MVP Phase (Months 1-3)
- 1 Full-Stack .NET Developer
- 1 Part-time Mobile Developer (.NET MAUI)
- 1 Part-time UI/UX Designer
- 1 Healthcare Compliance Consultant

### Growth Phase (Months 4-12)
- 2-3 Backend Developers (.NET/C#)
- 1 Frontend Developer (Blazor)
- 1 Mobile Developer (.NET MAUI)
- 1 DevOps Engineer (part-time)
- 1 Data Scientist (ML models)
- 1 Customer Support (part-time → full-time)
- 1 Marketing/Growth (contractor)
- 1 Compliance Officer (part-time)

---

## Development Roadmap

> Waves re-baselined August 2026: **R1 → Q4 2026, R2 → Q1 2027, R3 → Q2 2027, R4 → Q3 2027.** The [release matrix](./release_matrix.md) remains canonical for what ships in each wave.

### Built so far (as of August 2026)
- ✅ Core backend (.NET 10, EF Core, Cloud SQL PostgreSQL 16)
- ✅ Fitbit device integration — migration to the **Google Health API is done** (code + docs); Google console registration completed 2026-08-07, and the app is capped at 100 users until restricted-scope verification completes
- ✅ Database schema & migrations (deployed via the migrator Cloud Run Job)
- ✅ Worker ingestion: 10-minute wearable sync + daily orphan cleanup + daily baseline calculation + weekly device-sync audit
- ✅ Statistical + inactivity alerting in the Worker (deterministic producers, every environment)
- ✅ Push delivery spine: notification outbox + FCM HTTP v1 (APNs passthrough), escalation ladder, quiet hours
- ✅ AI pipeline running in dev: webhook receiver → Pub/Sub → aggregator → SSA pre-processing → MedGemma assessment, family digests, real-time heart-rate assessment (prod gated off)
- ✅ AI providers wired in the API: MedGemma (Ollama on Cloud Run) + Gemini 2.0 Flash (chat, insights, reports)
- ✅ Datadog APM with opt-in metrics (PR #4); atomic onboarding + orphaned-organization cleanup (PR #5); health-data disclosure banner on Web (PR #9 — a Google verification prerequisite; the mobile equivalent is pending)

### R1 — Q4 2026: MVP Launch
- 🔄 Blazor dashboard (basic features)
- 🔄 Statistical anomaly detection
- 🔄 Email/push alerts
- 🔄 Subscription management
- 🔄 Beta testing with 20 families
- 🔄 Public launch (BYOD model)

### R2 — Q1 2027: AI Pipeline & Multi-Device Start
- 🔄 AI pipeline rollout — Pub/Sub ingestion, SSA pre-processing + deterministic trend features, MedGemma inference (running in dev ahead of schedule; prod enablement remains — see [llm_design.md](./llm_design.md))
- ⏳ .NET MAUI mobile app (iOS & Android)
- ⏳ Garmin integration
- ⏳ Advanced dashboard features
- ⏳ Apply for device intraday access

### R3 — Q2 2027: Multi-Device Support
- ⏳ Apple Watch integration
- ⏳ Samsung Health integration
- ⏳ Device bundle option
- ⏳ Healthcare provider partnerships

### R4 — Q3 2027: Enterprise & Scale
- ⏳ Enterprise features (assisted living)
- ⏳ Mobile offline support (local SQLite cache) + HealthKit integration
- ⏳ Withings, Oura, Whoop support
- ~~Refined per-user LSTM risk models~~ — descoped 2026-08-10; replaced by qualitative trend interpretation (SSA + deterministic trend features read by MedGemma; no per-user models, no risk scores)
- ⏳ Telemedicine integration
- ⏳ Scale to 1,000+ users

---

## Success Criteria

### MVP Success (Month 3)
- [ ] 20+ beta families onboarded
- [ ] <10% false positive rate
- [ ] >95% data sync success
- [ ] >80% user satisfaction (NPS >50)

### Launch Success (Month 6)
- [ ] 100+ paying customers
- [ ] <5% churn rate
- [ ] $1,500+ MRR
- [ ] CAC <$50

### Growth Success (Month 12)
- [ ] 1,000+ paying customers
- [ ] <3% churn rate
- [ ] $15,000+ MRR
- [ ] LTV/CAC >3:1
- [ ] Support for 3+ device types

---

## Contact & Support

**Website**: https://carditrack.com
**Email**: info@carditrack.com
**Support**: support@carditrack.com
**GitHub**: https://github.com/Codesistance/product-carditrack

---

## License

Proprietary and confidential. All rights reserved.

---

**Last Updated**: August 14, 2026
**Version**: 1.2.0
**Status**: In Development
