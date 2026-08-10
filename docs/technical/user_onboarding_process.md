# USER ONBOARDING PROCESS - CARDITRACK

## OVERVIEW

CardiTrack supports **two organization types** with distinct onboarding flows:
1. **Family Accounts**: Individual/family monitoring elderly relatives
2. **Business Accounts**: Care homes and healthcare facilities with staff management

**Implemented today:** embedded Auth0 email/password auth with a hard email-verification gate, atomic organization + trial subscription + user setup (`POST /api/Onboarding/setup`), CardiMember creation, Fitbit device connection via Google OAuth (PKCE) with 10-minute polling ingestion, and daily pattern-baseline calculation, plus Google/Apple social login (app-side, 2026-08-10 — per-tenant Auth0 work pending). Notifications and billing are planned (marked below).

---

## ONBOARDING FLOW

### **STEP 1: AUTHENTICATION (Auth0)**

**Implemented today** — the mobile app uses Auth0's **embedded password-realm grant** with native screens, not the Universal Login redirect:

1. User opens `CreateAccountPage` and registers with email/password → `POST /dbconnections/signup` on the Auth0 tenant.
2. Sign-in posts credentials from `SignInPage` directly to `/oauth/token` (`grant_type=http://auth0.com/oauth/grant-type/password-realm`, realm `Username-Password-Authentication`), receiving access + refresh tokens in-app.
3. Password reset uses `POST /dbconnections/change_password` (Forgot Password flow).
4. The API validates the RS256 JWT on every request; identity is **token-derived on every request** via `UserContextMiddleware` (`sub` claim + database lookup) — there is no server session.

**Social login (wired 2026-08-10):** the Google and Apple buttons on `CreateAccountPage` and `SignInPage` launch Auth0 Universal Login in the system browser (Authorization Code + PKCE); same-email accounts are unified tenant-side by the post-login Action, with an API 409 backstop ([oauth_clients.md](./oauth_clients.md), runbook §8). Google works once the per-tenant Action/connection work is done; Apple awaits its credentials. Microsoft, Facebook, enterprise SSO (SAML/Azure AD/Okta), MFA, and passwordless are not part of the MVP.

#### **Email Verification Gate (mandatory, between account creation and onboarding)**

The tenant's post-login Action ([runbook §8](./auth0_setup_runbook.md)) **denies every unverified login** with the exact reason `email_not_verified`; the app matches that string and routes to `VerifyEmailPage`. Until the emailed link is clicked, the user cannot sign in and therefore cannot start onboarding.

- **Resend**: `VerifyEmailPage` calls `POST /api/v1/auth/resend-verification` (`AllowAnonymous`, rate-limited **5/hour/IP**, always answers success — no user enumeration).
- **Claim sync**: the Action stamps `https://carditrack.com/email_verified` into the access token; the API reads it in `UserContextMiddleware` and refreshes the stored `User.EmailVerified` flag on every `GET /api/Onboarding/status`.

---

### **STEP 2: ORGANIZATION TYPE SELECTION**

After first sign-in (verified email), new users select their account type on the native `AccountSetupPage`:

**Family Account:**
- Individual or family monitoring elderly relatives
- First user receives the `Member` role (role chip hidden in the family UI)
- Simplified caregiver relationship structure
- Consumer-focused pricing

**Business Account:**
- Care homes, healthcare facilities
- First user receives the `Admin` role; subsequent users get `Staff`
- Enterprise-level user management

Roles come from the `UserRole` enum: `Member` (1), `Admin` (2), `Staff` (3). **There is no Viewer role.**

**System Action:**
- The selection feeds the atomic setup call in Step 4 — organization, trial subscription, and user are created together (see below). `Organization` gets the selected type, `IsActive = true`, `CreatedDate`.

---

### **STEP 3: SUBSCRIPTION INITIALIZATION**

Created automatically inside the atomic setup transaction (`SubscriptionService.CreateTrialSubscriptionAsync`):

**Trial Setup (implemented today):**
- **Status**: `Trial` (first member of `SubscriptionStatus`)
- **Duration**: fixed **30 days** (`TrialEndDate = StartDate + 30`)
- **Tier**: hardcoded `Complete` during trial — tier selection/billing is planned
- **Limits are organization-type driven, not tier driven**:
  - Family: `MaxCardiMembers = 5`, `MaxUsers = 1`
  - Business: `MaxCardiMembers = 50`, `MaxUsers = 20`
- `BillingCycle = Monthly`, `Price = 0`, `Currency = "USD"`, `Features` JSON (all devices/alerts)
- There is **no Stripe or billing code** — payment collection is planned (Step 9)

**Tier names** (`SubscriptionTier`): `Basic`, `Complete`, `Plus`. Public pricing: Basic $8/mo (2 CardiMembers), Complete Care $15/mo (5 CardiMembers), annual billing −15%; Guardian Plus ($29.99) is a post-MVP business tier.

**Database:**
- Unique index on `OrganizationId` (1 subscription per org); `Status` and `(Status, EndDate)` indexed
- **FK with cascade delete to Organizations** (migration Aug 2026) — a subscription can never outlive its organization

---

### **STEP 4: USER ACCOUNT CREATION**

**Implemented today** — the preferred path is the **atomic** `POST /api/Onboarding/setup`, which creates the organization, trial subscription, and user **in one database transaction**:

- **Identity is token-derived**: `Auth0UserId` and `EmailVerified` come from the access token (never the request body); email comes from the token's email claim (body only as fallback); `Locale`/`TimeZoneId` are derived from the `Accept-Language` header (defaults `en-US`/`UTC`).
- **Idempotent on retry**: if a user with the same `Auth0UserId` already exists, the endpoint returns the existing organization + user instead of provisioning duplicates.
- **Race-safe**: concurrent retries that pass the existence check are stopped by the **unique filtered index** on `Users.Auth0UserId` (filtered to non-empty values — the PR #5 fix); the loser's insert fails and the winner's account is returned.
- **Legacy path**: the separate `POST /api/Onboarding/organization` → `POST /api/Onboarding/user` two-call flow still exists. A client dying between the calls can orphan an organization, which is why the atomic endpoint is preferred.
- **Safety net (PR #5)**: `OrphanedOrganizationCleanupWorker` runs daily at 03:00 UTC and deletes organizations older than 24 hours that have no users and no CardiMembers (FK cascade removes their trial subscriptions). Removals are logged at **Warning** because orphans mean a client bypassed the atomic endpoint and failed mid-onboarding.

**Role assignment (client-selected, server default `Member`):**
- **Family Account**: first user gets `Member`
- **Business Account**: first user gets `Admin`; subsequent users `Staff`

#### **User entity (actual schema)**

```
Users table
├── Id: Guid PK
├── OrganizationId: Guid (indexed)
├── Auth0UserId: string — UNIQUE FILTERED index (filter: not-empty; PR #5)
├── Email: string(255), UNIQUE
├── PasswordHash: string(500), required — legacy column; credentials are
│     Auth0-hosted and this column is pending removal (tracked code follow-up)
├── Name: string(200)
├── Phone: string(20), optional
├── Role: UserRole (stored as string; Member/Admin/Staff)
├── EmailVerified: bool (synced from the Auth0 claim)
├── LastLoginDate: DateTime?
├── HealthDataDisclosureDismissedDate: DateTime? (PR #9 — Google-required
│     disclosure banner dismissal; null = still show)
├── Locale: string(10), default "en-US" (derived from Accept-Language)
├── TimeZoneId: string(50), default "UTC"
├── IsActive: bool (soft delete)
└── CreatedDate / UpdatedDate

Indexes: UNIQUE(Email), OrganizationId, IsActive, UNIQUE FILTERED(Auth0UserId)
```

The entity has **no** `ProfilePictureUrl`, `AuthProvider`, or `Auth0Metadata` — those belonged to an earlier design and were never built.

#### **Authentication & Authorization (as wired)**

JWT bearer validation uses `Auth0:Domain` + `Auth0:Audience` (issuer, audience, lifetime, signing key all validated; zero clock skew). Registered policies: `RequireAdmin`, `RequireBusinessAccount`, `RequireFamilyAccount`, plus a global `FallbackPolicy` requiring an authenticated user. The three claim-based policies are **inert today** — the tenant does not issue `role`/`organization_type` claims yet ([runbook §13](./auth0_setup_runbook.md)).

**API behavior notes:**
- Global rate limits: 100 requests/minute and 1,000/hour per IP (resend-verification stricter at 5/hour).
- All 2xx responses wrap payloads in `ApiResponse<T>` (`{success, message, data, timestamp}`); errors use `ErrorResponse` (`{success:false, message, errors:[{field,message}], traceId, timestamp}`).
- **Enums serialize as integers** in JSON (no string-enum converter is registered).

---

### **STEP 5: CARDIMEMBER SETUP**

Users add the elderly person(s) to monitor (`POST /api/Onboarding/cardimember` — requires an organization; enforced via the token-derived user context):

**Personal Information (actual schema):**
- **Name**, **DateOfBirth** (DateOnly), **Gender** (Male/Female/Other/PreferNotToSay)
- **Email / Phone**: optional contacts
- **EmergencyContactName / EmergencyContactPhone**: two flat columns on `CardiMembers` (not JSON; a richer multi-contact entity is planned)
- **MedicalNotes**: intended to be encrypted at rest; **currently stored unencrypted** — only device OAuth tokens are AES-256-GCM encrypted today. Encryption of MedicalNotes is a tracked follow-up (see [data_protection_architecture.md](./data_protection_architecture.md)).

The add-member form localizes the emergency phone placeholder to the device region (PR #8).

**Relationship Definition (actual `UserCardiMembers` schema):**
```
UserCardiMembers (Many-to-Many Linking Table)
├── RelationshipType: Self, Parent, Spouse, Grandparent, Sibling, Child, Other
├── IsPrimaryCaregiver: bool
├── CanViewHealthData: bool (default true)
├── ReceiveAlerts: bool (default true)
├── NotificationPreferences: JSON — { "sms": true, "email": true, "push": false }
├── AssignedDate: DateTime
└── IsActive: bool
```

There are no `CanManageDevices` or `Permissions` columns — granular per-relationship permissions beyond the flags above are planned.

**Key Features:**
- One CardiMember can have multiple caregivers
- One caregiver can monitor multiple CardiMembers
- Primary caregiver designation for escalation

---

### **STEP 6: DEVICE CONNECTION**

Users connect health monitoring devices:

**Device Selection:**
Supports 8+ device types:
- Fitbit
- AppleWatch
- Garmin
- Samsung
- Withings
- Oura
- Whoop
- Other

> **Implemented today:** only the **Fitbit (Google Health API)** provider is registered in DI; the other providers exist as configuration stubs only.

**OAuth Authorization Flow:**

> Fitbit/Pixel Watch connections go through the **Google Health API** (the legacy Fitbit Web API is decommissioned September 2026). Authorization uses Google OAuth 2.0 — the wearer signs in with the Google account their Fitbit is linked to.

```
1. User clicks "Connect Fitbit"
   ↓
2. System generates an opaque state token + PKCE pair, cached server-side
   (single-use, 15-minute TTL) with the initiating user/member/provider
   ↓
3. Redirect to Google OAuth consent page
   https://accounts.google.com/o/oauth2/v2/auth?
     response_type=code
     client_id={ClientId}
     redirect_uri=https://api.carditrack.com/api/v1/oauth/redirect/fitbit
     scope=https://www.googleapis.com/auth/googlehealth.health_metrics_and_measurements.readonly
           https://www.googleapis.com/auth/googlehealth.activity_and_fitness.readonly
           https://www.googleapis.com/auth/googlehealth.sleep.readonly
     (full scope URIs, space-delimited and URL-encoded in the real request)
     state={opaque server-cached token}
     code_challenge={S256 challenge}&code_challenge_method=S256
     access_type=offline
     prompt=consent
   ↓
4. User approves permissions on Google
   ↓
5. Google redirects to the API bounce endpoint (web OAuth clients require an
   https redirect), which 302s back into the app deep link with code + state:
   GET /api/v1/oauth/redirect/fitbit → 302 carditrack://oauth/callback?code=...
   (the bounce only ever redirects into the carditrack:// scheme — any other
   target would be an open redirect leaking code+state)
   ↓
6. System exchanges code for access/refresh tokens (with the PKCE verifier)
   POST https://oauth2.googleapis.com/token
   Body: grant_type=authorization_code&code={code}&redirect_uri={RedirectUri}
         &client_id={ClientId}&client_secret={ClientSecret}
   ↓
7. Save encrypted tokens to database (Cloud SQL PostgreSQL, DeviceConnections)
   ↓
8. Initial sync runs; thereafter the connection is picked up by the 10-minute
   polling cycle (webhook push ingestion is planned, not built)
   ↓
9. Notify family: "Fitbit Connected!"
```

**Database Storage:**
```csharp
DeviceConnection Entity:
├── CardiMemberId: Link to monitored individual
├── DeviceType: Enum (Fitbit, AppleWatch, etc.)
├── AccessToken: Encrypted (AES-256-GCM)
├── RefreshToken: Encrypted (AES-256-GCM)
├── TokenExpiry: DateTime (UTC)
├── ConnectionStatus: Connected, Disconnected, TokenExpired, AuthError, SyncError
├── SyncFrequencyMinutes: default 30
└── LastSyncDate: DateTime (UTC) — timestamp of the last successful polling sync
```

(`ConnectionStatus` has no `Pending` value.)

**Permission Scoping:**
Google Health API scope bundles requested (full form `https://www.googleapis.com/auth/googlehealth.<bundle>`):
- `activity_and_fitness.readonly`: Steps, distance, active minutes
- `health_metrics_and_measurements.readonly`: Heart rate (incl. intraday), HRV, SpO2
- `sleep.readonly`: Duration, efficiency, sleep stages
- `ecg.readonly` / `irn.readonly` (later phase): ECG readings, irregular rhythm notifications

> All Google Health API scopes are **Restricted** — production access requires Google's privacy & security review; pre-verification, only enrolled test users can connect.

**Google Verification Prerequisites (public-launch gate):**

Unverified apps are capped at 100 connected users — enough for dev and beta, but public launch requires passing both gates below (combined runway ~4–8 weeks; see [app verification](https://developers.google.com/health/app-verification)).

*Gate 1 — OAuth restricted-scope review (Google Trust & Safety):*
- [ ] Domain ownership verified in Google Search Console (`carditrack.com`, cloud-ops Google account)
- [ ] Public homepage on the verified domain — reachable without login, same app name/branding as the OAuth consent screen, describes the health-data functionality, prominently links the privacy policy
- [ ] Privacy policy on the same domain with a **dedicated Google Health API section** (not blended into generic disclosures): data collected (heart rate incl. intraday, HRV, SpO2, activity, sleep), purposes (anomaly alerts, daily digests, trend monitoring), sharing (authorized family members only — no ads, no resale), retention/deletion, and the Limited Use affirmation: *"CardiTrack's use and transfer of information received from Google APIs adheres to the [Google API Services User Data Policy](https://developers.google.com/terms/api-services-user-data-policy), including the Limited Use requirements."*
- [ ] Terms of service page linked from the consent screen
- [x] In-app disclosure on the **web dashboard** — shipped (PR #9): `HealthDataDisclosureBanner` shows Google's prescribed format verbatim (*"CardiTrack collects health and fitness data to enable anomaly alerts, daily health digests, and trend monitoring."*), show-once and dismissible (`HealthDataDisclosureDismissedDate`); renders only for authenticated users
- [ ] In-app disclosure in the **mobile app** — still missing
- [ ] Per-scope written justification tied to user-facing features
- [ ] Screen recording of the full OAuth consent flow and where health data surfaces in the app

*Gate 2 — CASA security assessment:*
- [ ] Annual assessment by an authorized third-party lab (self-scan not accepted; ~$500–$4,500, 2–6 weeks) → Letter of Assessment submitted to Google, renewed every 12 months

**Data Ingestion & Token Management (implemented today):**
- **10-minute polling** — `WearableSyncWorker` (in `CardiTrack.Worker`, cron `0 */10 * * * *`) finds connections due for sync and pulls data through the keyed per-provider sync service. There is no webhook ingestion.
- **Token refresh** — happens **inside the sync path**: `OAuthTokenRefreshService` refreshes expiring OAuth tokens as part of each sync. There is no separate token-refresh worker.
- **Planned**: Google Health API webhook push (notify-then-fetch) feeding the AI pipeline (see [llm_design.md](../llm_design.md)), and a `device_disconnected` alert when a device goes quiet.

---

### **STEP 7: NOTIFICATION PREFERENCES**

> **Status: Planned — not yet implemented.** No SMS, email, or push notification delivery code exists (no Twilio, SendGrid, or SignalR anywhere in the stack). What exists today is the `NotificationPreferences` JSON column on `UserCardiMembers` (`{"sms":..., "email":..., "push":...}`) and the `ReceiveAlerts` flag.

**Planned alert types:**
1. **Inactivity Alerts**: Steps < 50% baseline for 2+ days
2. **Heart Rate Alerts**: Resting HR >15% above baseline for 3+ days
3. **Sleep Disruption**: Sleep efficiency < 70% for 5+ days
4. **Sudden Pattern Break**: No morning activity by 11am
5. **Long-term Trends**: Declining mobility over 4 weeks

**Alert Severity Levels** (`AlertSeverity`, displays as color names):
- **Green**: Informational, no action needed
- **Yellow**: Minor deviation, "worth a call"
- **Orange**: Concerning pattern, "consider doctor visit"
- **Red**: Urgent, "please check on them"

**Planned customization:** sensitivity tuning (z-score thresholds), quiet hours, escalation rules, per-alert-type enable/disable.

---

### **STEP 8: BASELINE ESTABLISHMENT**

> **Status: Implemented.** `BaselineCalculationWorker` (`CardiTrack.Worker`) writes `PatternBaselines` daily using `BaselineCalculator` (`CardiTrack.Application`). Alert generation from these baselines is still planned — see STEP 7.

**Learning period:**
- **Duration**: 30, 60 and 90-day baselines are written per member
- **Coverage gate**: a period needs data on **80% of its days** (24 of 30) before any baseline is written. Until the 30-day baseline exists, `DashboardService` reports the member as still learning and the app shows the "getting to know {Name}" state.
- **Frequency**: recalculated daily (02:30 UTC), appended rather than replaced so baseline drift stays visible

**Pattern Baseline Calculation:**
```csharp
PatternBaseline Entity:
├── CardiMemberId: Link to monitored individual
├── CalculatedDate: When baseline was computed
├── PeriodDays: 30, 60, or 90 days
│
├── Activity Patterns:
│   ├── AvgSteps: Mean daily steps
│   ├── StdDevSteps: Standard deviation (for z-score)
│   ├── AvgActiveMinutes: Mean active minutes/day
│   └── StepsByDayOfWeek: JSON array [Mon: 5000, Tue: 4800, ...]
│
├── Heart Rate Patterns:
│   ├── AvgRestingHeartRate: Mean resting HR
│   ├── StdDevHeartRate: Standard deviation
│   └── MaxHeartRateObserved: Highest HR recorded
│
└── Sleep Patterns:
    ├── AvgSleepMinutes: Mean sleep duration
    ├── TypicalBedtime: Time (HH:MM)
    ├── TypicalWakeTime: Time (HH:MM)
    └── AvgSleepEfficiency: Mean efficiency %
```

Each metric is gated on **7 samples of its own** and left null when thinner — ingestion populates metrics unevenly, so a member can have steps every day and sleep on half of them. Spread is the **sample** standard deviation (n−1), bedtime/wake time are **circular** means over the 24-hour clock (an arithmetic mean of 23:40 and 00:20 is midday) reported in UTC, and weekday buckets are null rather than zero when unsampled. See [worker readme](../apps/worker/readme.md#baselinecalculationworker).

**AI/ML Pattern Analysis (planned):**
- **Algorithm**: statistical thresholds at R1; SSA-LSTM + MedGemma from R2 (see [llm_design.md](../llm_design.md))
- **Z-Score Calculation**: (TodayValue - Baseline) / StdDev; |Z| > 2.0 triggers alert
- **Day-of-Week Awareness** and 7-day rolling trend detection

---

### **STEP 9: TRIAL PERIOD & CONVERSION**

**Implemented today:** every organization gets a 30-day trial (`Status = Trial`, `TrialEndDate` tracked, tier `Complete`, price $0). Nothing enforces or converts the trial yet.

> **Status: Planned — not yet implemented.** Trial-expiration reminders, payment collection, tier selection, invoicing, and suspension are future work. There is no Stripe or billing integration.

**Planned conversion flow:**
```
1. User selects subscription tier
   ├── Basic: $8/month (2 CardiMembers)
   ├── Complete Care: $15/month (5 CardiMembers)
   └── (annual billing −15%; Guardian Plus $29.99 is a post-MVP business tier)

2. Payment collection (PCI-DSS-compliant tokenization)

3. Subscription activation: Trial → Active, billing dates set

4. Failed conversion: Trial → Suspended, 7-day read-only grace,
   90-day retention before soft delete
```

---

## SECURITY & COMPLIANCE FEATURES

### **HIPAA Compliance**

**Data Encryption:**
- **At Rest**: Cloud SQL for PostgreSQL encryption at rest (Google-managed)
  - Device OAuth tokens: AES-256-GCM application-level encryption (implemented)
  - Medical notes: AES-256-GCM application-level encryption (implemented in `CardiMemberService`; see [data_protection_architecture.md](./data_protection_architecture.md))
- **In Transit**: HTTPS/TLS 1.2+ for all connections
- **Backups**: automated encrypted Cloud SQL backups

**Access Controls:**
- **Role-Based Access Control (RBAC)**: Member, Admin, Staff roles (claim-based policies registered but inert until the tenant issues role claims)
- **Relationship-Scoped Access**: Caregivers only see assigned CardiMembers
- **Email-verification gate**: unverified accounts cannot log in at all

**Audit Trail:**
```csharp
AuditLog Entity (6-year retention policy; deployed infra currently retains 30 dev / 90 prod via tfvars):
├── UserId: Who accessed
├── CardiMemberId: Whose data was accessed
├── Action: ViewDashboard, ViewAlert, ExportData, etc.
├── Timestamp: When (UTC)
├── IpAddress: From where
├── UserAgent: Browser/device info
└── DataAccessed: JSON (specific fields viewed)
```
(Written by `AuditLoggingMiddleware` for endpoints carrying the opt-in `AuditHealthDataAccessAttribute` — the six health-data controllers; unannotated endpoints such as Onboarding are not audited.)

**Business Associate Agreements (BAAs):**
- ✅ **Auth0**: BAA required before prod go-live ([runbook §1](./auth0_setup_runbook.md))
- ✅ **Google Cloud**: covers Cloud Run, Cloud SQL, GCS, Secret Manager
- ❌ **Google Health API** (Fitbit/Pixel Watch data): does NOT provide BAA (user consent model)
- Notification vendors (SMS/email): to be selected when notifications are built

### **Data Protection**

**Soft Deletes:**
- `IsActive` flag on Organizations, Users, CardiMembers, UserCardiMembers, DeviceConnections, Alerts, Devices
- No hard deletions to preserve audit trail (exception: orphaned-organization cleanup, which removes only orgs that never completed onboarding)

**Foreign Keys:**
- One FK exists: **Subscriptions → Organizations with cascade delete** (migration Aug 2026), so orphan cleanup is a single organization delete. All other relationships use Guid references with application-level integrity.

**Authentication Security (Auth0):**
- **Password Hashing**: Bcrypt with salt (managed by Auth0)
- **Credentials are Auth0-hosted** — a legacy `PasswordHash` column remains on the `Users` table pending removal (tracked code follow-up); it is never populated with a real hash
- **Breached Password Detection**: enabled in prod (Auth0 attack protection)
- **JWT Tokens**: 1-hour access tokens; rotating refresh tokens (30-day absolute / 15-day inactivity)
- **No Credentials in Logs**: auth tokens never logged

---

## TECHNICAL ARCHITECTURE SUMMARY

### **Database Tables Involved in Onboarding** (Cloud SQL PostgreSQL 16)

```
Organizations
├── Id, Name, Type (Family/Business), IsActive, CreatedDate, UpdatedDate

Users
├── Id, Auth0UserId (UNIQUE FILTERED), Email (UNIQUE), PasswordHash (legacy,
│     pending removal), Name, Phone, Role, EmailVerified, LastLoginDate
├── HealthDataDisclosureDismissedDate, Locale, TimeZoneId, OrganizationId
└── IsActive, CreatedDate, UpdatedDate

CardiMembers
├── Id, OrganizationId, Name, Email, Phone, DateOfBirth, Gender
├── EmergencyContactName, EmergencyContactPhone, MedicalNotes (encryption planned)
└── LastSyncDate, IsActive, CreatedDate, UpdatedDate

UserCardiMembers (Relationship Table)
├── Id, UserId, CardiMemberId, RelationshipType, IsPrimaryCaregiver
├── CanViewHealthData, ReceiveAlerts, NotificationPreferences (JSON)
└── AssignedDate, IsActive, CreatedDate

Subscriptions
├── Id, OrganizationId (UNIQUE, FK→Organizations CASCADE), Tier, Status
├── StartDate, EndDate, TrialEndDate, Price, Currency, BillingCycle
└── MaxCardiMembers, MaxUsers, Features (JSON), PaymentMethod (JSON)

DeviceConnections
├── Id, CardiMemberId, DeviceType, DeviceName, IsPrimary, ConnectionStatus
├── AccessToken (ENCRYPTED), RefreshToken (ENCRYPTED), TokenExpiry, Scopes (JSON)
└── ConnectedDate, LastSyncDate, SyncFrequencyMinutes (default 30), Metadata (JSON)

Devices (Reference Table)
├── Id, DeviceType, Manufacturer, ModelName, DisplayName, Capabilities (JSON)
└── ApiEndpoint, OAuthConfig (JSON), SortOrder, IconUrl

ActivityLogs (Populated by polling sync)
├── Id, CardiMemberId, DeviceConnectionId, DataSource, Date
└── ~25 nullable metrics: Steps, HeartRate, Sleep stages, SpO2, VO2Max, ...

PatternBaselines (Written daily by BaselineCalculationWorker)
Alerts (Planned generation — table exists)
AuditLogs (Written by AuditLoggingMiddleware on PHI access)
```

### **Background Jobs (CardiTrack.Worker — Cronos)**

Non-AI background jobs run in `CardiTrack.Worker` as `CronBackgroundService` subclasses. **Exactly four workers exist today** (6-field cron, Cronos IncludeSeconds, configured in appsettings):

```csharp
// Every 10 minutes — polls device connections due for sync; token refresh
// happens inside the sync path (OAuthTokenRefreshService)
public class WearableSyncWorker : CronBackgroundService              // "0 */10 * * * *"

// Daily 03:00 UTC — deletes orphaned organizations (>24h, no users/members);
// removals logged at Warning (PR #5 safety net)
public class OrphanedOrganizationCleanupWorker : CronBackgroundService // "0 0 3 * * *"

// Daily 02:30 UTC — recalculates 7/14-day provisional and 30/60/90-day pattern baselines
public class BaselineCalculationWorker : CronBackgroundService       // "0 30 2 * * 0"

// Weekly Sunday 04:00 UTC — re-fetches a random sample of connections over a
// wide window to measure how far back providers revise data (observation only)
public class DeviceSyncAuditWorker : CronBackgroundService           // "0 0 4 * * 0"
```

> **Planned workers (not yet built):** trial-expiration reminders, retention/cleanup. The AI pipeline's scheduled jobs (aggregation, predictive batch, digests) belong to the planned GCP ingestion pipeline — see [llm_design.md](../llm_design.md).

---

## USER ONBOARDING CHECKLIST

### **Family Member Actions:**
- [ ] **Create account** with email/password (Google/Apple buttons exist but are not yet functional)
- [ ] **Verify email** via the emailed link (mandatory — login is blocked until verified; resend available)
- [ ] **Sign in** and complete setup: select organization type (Family/Business)
- [ ] **Add CardiMember profile** (name, DOB, gender, emergency contact, medical notes)
- [ ] **Define relationship** (Parent, Spouse, Grandparent, etc.)
- [ ] **Connect device** (Fitbit via Google OAuth today)
- [ ] *(Planned)* Configure notification preferences, review baseline progress, select paid tier

### **Elderly Person Actions:**
- [ ] Review privacy notice (what family will see)
- [ ] Click "Connect Fitbit" button
- [ ] Log into Google account linked to the device (Google OAuth)
- [ ] Approve data access permissions
- [ ] Confirm connection success

### **System Actions (Automated, implemented):**
- [ ] **Register account** via Auth0 `/dbconnections/signup`
- [ ] **Deny unverified logins** (post-login Action, `email_not_verified`)
- [ ] **Validate JWT** on every request; derive identity from the `sub` claim (`UserContextMiddleware`)
- [ ] **Atomic setup**: Organization + trial Subscription + User in one transaction (`POST /api/Onboarding/setup`), idempotent on retry
- [ ] **Sync EmailVerified** claim on `GET /api/Onboarding/status`
- [ ] **Create CardiMember** and `UserCardiMembers` link
- [ ] **Generate opaque OAuth state + PKCE** (single-use, 15-min TTL) for device connection
- [ ] **Bounce** Google's redirect into the `carditrack://` deep link
- [ ] **Exchange OAuth code, encrypt and store device tokens** (AES-256-GCM)
- [ ] **Poll device data every 10 minutes**; refresh OAuth tokens in the sync path
- [ ] **Clean up orphaned organizations** daily (03:00 UTC)
- [ ] **Recalculate pattern baselines** daily (02:30 UTC); **audit provider revision windows** weekly (Sunday 04:00 UTC)
- [ ] **Write audit-log entries** for annotated health-data endpoints (`AuditLoggingMiddleware` + `AuditHealthDataAccessAttribute`)

### **System Actions (Planned):**
- [ ] Welcome email; device connection invitations; webhook ingestion
- [ ] AI anomaly detection
- [ ] Trial expiration reminders; trial conversion/suspension

---

## SUCCESS METRICS

**Onboarding Completion Rate:**
- Target: >80% of signups complete full onboarding
- Track drop-off at each step:
  - Account creation → email verified: >90%
  - Email verified → CardiMember setup: >95%
  - CardiMember setup → Device connected: >70% (hardest step)
  - Device connected → Baseline established: >95%
  - Trial → Paid conversion: >60%

**Time to Value:**
- Account creation → First device connection: <24 hours
- Device connection → First synced data: <10 minutes (initial sync + 10-minute polling cycle)
- Trial start → Baseline established: 30 days (planned)

---

## ONBOARDING ENHANCEMENTS (FUTURE)

**Gamification:**
- Progress bar: "3 of 5 steps complete"
- Checklist with completion badges
- Email nudges for incomplete onboarding

**Guided Tour:**
- Interactive dashboard walkthrough
- Video tutorials for device connection
- FAQ chatbot for common questions

**White-Glove Onboarding (Premium Tier):**
- Dedicated onboarding specialist
- Phone call to elderly person to assist with Fitbit connection
- Custom baseline tuning based on medical history

**Multi-Language Support:**
- Spanish, Mandarin, French (high elderly populations)
- Localized date/time formats (Locale/TimeZoneId columns already exist)
- Cultural sensitivity in messaging

---

## SUMMARY

The CardiTrack user onboarding process is designed to be **secure, compliant, and user-friendly**, balancing the needs of family caregivers (ease of use) with elderly users (simplicity, privacy) while maintaining HIPAA compliance.

**Key Success Factors:**
1. **Low friction**: atomic single-call setup after sign-in; no orphaned half-created accounts
2. **Auth0 integration**: credentials Auth0-hosted, hard email-verification gate
3. **Security first**: encrypted device tokens, rate limiting, opaque single-use OAuth state with PKCE
4. **Privacy transparency**: clear explanation of what family sees; Google-format health-data disclosure (web shipped, mobile pending)
5. **Immediate value**: first device data within one polling cycle (10 minutes)

**Authentication today:** email/password via Auth0's embedded password-realm grant, with mandatory email verification; Google/Apple social login via Universal Login + PKCE with tenant-side account linking (app code shipped 2026-08-10; per-tenant Action/connection deploy pending, Apple credentials pending). **Planned:** enterprise SSO, MFA.

**Critical Path:**
Create Account (Auth0 signup) → Verify Email (mandatory gate) → Sign In → Atomic Setup (Organization + Trial + User) → CardiMember Setup → Device Connection (Fitbit via Google OAuth + PKCE) → 10-minute polling ingestion → *(planned)* Notifications → Baseline → Paid Conversion

---

**Last Updated:** August 7, 2026

**END OF ONBOARDING DOCUMENTATION**
