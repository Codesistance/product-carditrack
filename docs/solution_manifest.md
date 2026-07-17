# CardiTrack Solution Manifest

## Executive Summary

**CardiTrack** is a multi-device elderly health monitoring platform that provides affordable, preventive health monitoring for families using existing wearable devices with AI-powered pattern analysis.

**Core Value Proposition:** Family members get peace of mind and early warning of health issues at $8-15/month (vs $40-70/month for medical alert systems), using hardware their elderly parents likely already own.

**Target Market:** 150M+ adults aged 65+ across the US and EU, and their family caregivers — 53M in the US *(AARP/NAC, 2020)* and 44M in the EU *(Eurocarers)* — who bear primary responsibility for monitoring elderly relatives living independently.

---

## Product Vision

### Mission Statement

To empower families with affordable, preventive health monitoring for their elderly loved ones, enabling early detection of health concerns and peace of mind through intelligent pattern analysis.

### Core Differentiators

1. **Preventive vs Reactive**: Catches health issues BEFORE emergencies (not just fall detection)
2. **Affordable**: 50-70% cheaper than medical alert systems
3. **Non-Intrusive**: Uses existing devices, not new medical equipment
4. **AI-Powered**: Learns individual baselines, reduces false alerts
5. **Device-Agnostic**: Works with Fitbit, Apple Watch, Garmin, Samsung, and more
6. **No Hardware Lock-in**: Works with devices people already own

---

## Business Model

### Pricing Tiers

> Plan limits below are canonical and match the [Subscription API](./execution/backend/api/subscriptions.md). All consumer plans start with a **30-day free trial**.

#### Tier 1: "Basic" - $8/month ($81.60/year)
- Bring your own wearable device
- Daily activity dashboard
- Email alerts for major deviations
- Up to **2 CardiMembers**
- Up to **5 family members**
- Standard alert types
- **90-day** data history
- No data export

#### Tier 2: "Complete Care" - $15/month ($153/year)
- Support for any supported device
- Real-time SMS/email/push alerts
- Weekly health reports
- Advanced AI alert types (pattern analysis)
- Up to **5 CardiMembers**
- Up to **20 family members**
- **365-day** data history
- Multi-device support per member
- PDF, CSV, and FHIR R4 data export

#### "Guardian Plus" - $29.99/month *(post-MVP — business tier)*
- Not part of the consumer MVP; handled via a dedicated business account flow (assisted living facilities, care homes)
- Everything in Complete Care, plus: 24/7 monitoring dashboard, unlimited family member access, unlimited CardiMembers, telemedicine integration, priority support, 2-year data history, API access

### Device Bundle Option (Add-on)
- **Fitbit Charge 6 Bundle**: +$100 upfront (includes device)
- **Annual Subscription**: 15% discount on all consumer tiers (Complete Care: $153/year, saves $27)

### Unit Economics (Tier 2 Example)

```
Monthly revenue per user: $15
Monthly costs per user: ~$2 (hosting, SMS, support)
Monthly margin per user: $13

Customer LTV (churn-derived, $13/month margin):
  At 5% monthly churn (launch target):  avg lifetime ~20 months → LTV ≈ $260
  At 3% monthly churn (growth target):  avg lifetime ~33 months → LTV ≈ $430
  LTV target of >$300 therefore assumes churn below ~4%/month.

With Device Bundle:
  Hardware cost (bulk): ~$100, covered by the +$100 upfront bundle price
  → hardware is margin-neutral at purchase; subscription margin unchanged
```

---

## Technical Architecture

### Technology Stack

**Backend (transactional core):**
- .NET 10 (ASP.NET Core Web API)
- Entity Framework Core
- Azure SQL (system of record — identity, organizations, subscriptions, health data, audit)
- .NET Worker Service + Cronos (**non-AI background jobs only**: OAuth token refresh, baseline recalculation, cleanup)

**AI pipeline (target architecture — see [llm_design.md](./llm_design.md)):**
- Fitbit Subscriptions API (webhook push — no polling)
- Azure Event Hubs (raw event buffer)
- Azure Functions, dotnet-isolated (aggregation, SSA-LSTM pre-processing, severity routing, digests)
- MedGemma 1.5 4B on Azure Container Apps GPU (vLLM, OpenAI-compatible endpoint)
- Azure Cosmos DB (AI pipeline outputs: results, prediction cards, trends)
- Azure Notification Hubs (FCM/APNs push routing)

**Frontend:**
- Blazor Server (web dashboard)
- .NET MAUI (cross-platform mobile app)
- Bootstrap 5 (UI framework)
- SignalR (real-time updates)

**Infrastructure:**
- Azure Cloud Platform
- Terraform (Infrastructure as Code)
- Docker (containerization)
- GitHub Actions (CI/CD)

**External Integrations:**
- Fitbit Web API
- Apple HealthKit
- Garmin Connect API
- Samsung Health SDK
- Withings API
- Twilio (SMS)
- SendGrid (Email)
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
│             (SQL Server / PostgreSQL)                       │
│  - Organizations / Users / CardiMembers                     │
│  - Device Connections (Multi-device support)                │
│  - Activity Logs / Baselines / Alerts                       │
│  - Audit Logs (HIPAA compliance)                            │
└─────────────────────────────────────────────────────────────┐
                            │
                            ↓
┌─────────────────────────────────────────────────────────────┐
│         NON-AI BACKGROUND JOBS (CardiTrack.Worker)          │
│  - Token Refresh (OAuth management, every 4 hours)          │
│  - Baseline Recalculation (weekly)                          │
│  - Data retention / cleanup jobs                            │
└─────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────┐
│        AI INGESTION & INFERENCE PIPELINE (see llm_design)   │
│                                                             │
│  Device webhooks → Event Hubs → Azure Functions (5-min      │
│  aggregation + SSA-LSTM) → MedGemma 1.5 4B (ACA GPU/vLLM)   │
│  → Cosmos DB (results) → Severity router → Alerts/Digests   │
└─────────────────────────────────────────────────────────────┘
```

> Ingestion is **webhook push** (Fitbit Subscriptions API) with 5-minute aggregation windows — there is no polling sync loop. The Worker Service hosts only non-AI jobs.

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
        (SSA-LSTM pre-processing + MedGemma 1.5 4B)
                    ↓
        Contextual Family Alerts
        (SMS, Email, Push Notifications)
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

**Technology:** MedGemma 1.5 4B (medical LLM) with SSA-LSTM pre-processing — see [llm_design.md](./llm_design.md) for the full pipeline design. The MVP launches with statistical threshold alerts; the MedGemma pipeline replaces them per the [release matrix](./release_matrix.md).

**Algorithms:**
- **Signal decomposition**: SSA (Singular Spectrum Analysis) — separates trend, oscillation, and noise per metric
- **Time-series forecasting**: per-user LSTM (trend prediction and 24–72h risk scoring)
- **Anomaly assessment**: MedGemma 1.5 4B interprets denoised trends and anomaly scores, assigns severity

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
- Offline support with local SQLite cache
- Platform-specific integrations (HealthKit on iOS)

### 5. HIPAA Compliance

**Technical Safeguards:**
- ✅ Encryption at rest (Azure SQL TDE)
- ✅ Encryption in transit (TLS 1.2+)
- ✅ Field-level encryption (OAuth tokens, medical notes)
- ✅ Access controls (RBAC, MFA for admins)
- ✅ Token policy: short-lived access tokens (15–60 min) with rotating refresh tokens (30-day absolute lifetime); ~15-minute idle timeout on web; biometric re-authentication gate on mobile app open
- ✅ Comprehensive audit logging

**Administrative Safeguards:**
- Privacy policies
- Security policies
- Breach notification procedures
- Workforce training program
- Business Associate Agreements (BAAs) with all vendors

**Audit Logging:**
- All PHI access tracked
- User ID, CardiMember ID, action, timestamp
- IP address and user agent tracking
- **6-year retention** (1 year in hot storage, then archive tier)
- Exportable audit trail for compliance

---

## Data Model

### Core Entities

**Organization**
- Multi-tenant support
- Types: Family, Business
- Subscription management

**User**
- Family members/caregivers
- Roles: Admin, Staff, Viewer
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
- Recalculated weekly
- Day-of-week variations

**Alert**
- Generated by pattern analysis
- Alert severity levels: Yellow (Caution), Orange (Urgent), Red (Critical); Green is a *health status*, not an alert severity — alerts exist only for non-green states
- Acknowledgment tracking
- Resolution workflow (`new → acknowledged → resolved`)

**AuditLog**
- HIPAA compliance tracking
- All PHI access logged
- 6-year retention (1 year hot, then archive)

---

## Go-to-Market Strategy

### Phase 1: MVP Launch (Months 1-3)

**Month 1: Build MVP**
- Core .NET backend with Fitbit integration
- Basic Blazor dashboard
- Simple alert rules (statistical, no ML yet)
- Database schema and migrations

**Month 2: Beta Testing**
- Recruit 10-20 families (friends, family, local community)
- Use Fitbit "Personal" app type for testing
- Collect feedback on alert accuracy
- Iterate on UX based on feedback

**Month 3: Apply for Device Approvals**
- Submit Fitbit intraday access request
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

### Cloud Infrastructure (Azure)

**MVP Phase (0-100 users):**
- App Service (Basic B1 — API): $13/month
- Azure SQL (Basic): $5/month
- Worker (App Service Basic B1): $13/month
- **Total**: ~$31-35/month

**Growth Phase (1,000-10,000 users):**
- App Service (Premium P1V2 — API): $146/month
- Azure SQL (Standard S2): $75/month
- Worker (Container App): ~$30-50/month
- SignalR (Standard): $50/month
- **AI pipeline** (from AI rollout — see [llm_design.md](./llm_design.md)):
  - ACA T4 GPU, 1 replica always-on: ~$430/month
  - Event Hubs (Standard): ~$11/month
  - Cosmos DB (serverless): ~$25/month
  - Notification Hubs: ~$6/month
  - Azure Functions (consumption): near-zero at this scale
- **Total**: ~$775-795/month (incl. AI pipeline)
- **Per user cost at 10,000 users**: ~$0.08/month
- **Margin**: ~$14.9/user/month (Tier 2)

### Database Storage

**Per CardiMember Per Year:**
- Activity logs: 365 rows × ~500 bytes = 183 KB
- Pattern baselines: 12 rows × 2 KB = 24 KB
- Alerts: ~50 rows × 1 KB = 50 KB
- **Total**: ~260 KB/member/year

**10,000 CardiMembers:**
- Data: 2.6 GB/year
- With indexes: ~5 GB/year
- Azure SQL Standard S2 (250 GB): Ample headroom

### Scaling Strategy

**Horizontal Scaling:**
- Auto-scale App Service based on CPU (>70%) and memory (>80%)
- Worker Service scales by replica count (Azure Container Apps)
- Max instances: 10 (API), configurable replicas (Worker)

**Database Scaling:**
- Read replicas for dashboard queries
- Partition ActivityLogs by CardiMemberId
- Archive logs >2 years to cold storage (Azure Blob)

**Caching:**
- Redis for user sessions and dashboard data
- In-memory cache for reference data
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
- **Mitigation**: Proactive token refresh every 4 hours
- **Monitoring**: Alert team if refresh rate drops

### Business Risks

**Risk 1: Market Rejection**
- **Mitigation**: Focus on "peace of mind" not "surveillance"
- **Validation**: Beta test with real families

**Risk 2: Competitor Launches Similar Feature**
- **Mitigation**: Move fast, build brand, capture market share
- **Strategy**: Position for acquisition by Google/Fitbit/Apple

**Risk 3: Regulatory Changes**
- **Mitigation**: HIPAA compliance from day 1
- **Legal**: Regular healthcare attorney consultations

### HIPAA/Legal Risks

**Risk 1: Data Breach**
- **Mitigation**: Multi-layer encryption, regular audits
- **Insurance**: Cyber liability with HIPAA coverage ($1-2M)

**Risk 2: Unauthorized Access**
- **Mitigation**: RBAC, audit logging, MFA
- **Monitoring**: Real-time suspicious access alerts

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

### Q1 2026 (Months 1-3): MVP Development
- ✅ Core backend (.NET 10, EF Core, Azure SQL)
- ✅ Fitbit integration (OAuth, data sync)
- ✅ Blazor dashboard (basic features)
- ✅ Statistical anomaly detection
- ✅ SMS/Email alerts
- ✅ Database schema & migrations
- 🔄 Beta testing with 20 families

### Q2 2026 (Months 4-6): Public Launch
- 🔄 AI pipeline rollout — Event Hubs ingestion, SSA-LSTM pre-processing, MedGemma inference (see [llm_design.md](./llm_design.md))
- 🔄 .NET MAUI mobile app (iOS & Android)
- 🔄 Advanced dashboard features
- 🔄 Subscription management
- 🔄 Apply for device intraday access
- 🔄 Public launch (BYOD model)

### Q3 2026 (Months 7-9): Multi-Device Support
- ⏳ Apple Watch integration
- ⏳ Garmin integration
- ⏳ Samsung Health integration
- ⏳ Device bundle option
- ⏳ Healthcare provider partnerships

### Q4 2026 (Months 10-12): Enterprise & Scale
- ⏳ Enterprise features (assisted living)
- ⏳ Withings, Oura, Whoop support
- ⏳ Refined per-user LSTM risk models (predictive monitoring at scale)
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
**GitHub**: https://github.com/marigbede/CardiTrack

---

## License

Proprietary and confidential. All rights reserved.

---

**Last Updated**: February 24, 2026
**Version**: 1.0.0
**Status**: In Development
