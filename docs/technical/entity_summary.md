# CardiTrack Entity Summary

This document provides an overview of all domain entities in the CardiTrack system. All entities live in **PostgreSQL 16 on GCP Cloud SQL**, the transactional system of record; the planned AI pipeline's outputs are documented separately in [llm_design.md](../llm_design.md). Field-level protection (what is encrypted, and what is planned to be) is covered in [data_protection_architecture.md](./data_protection_architecture.md).

**Implemented today:** 25 entity classes and **32** enums exist in `CardiTrack.Domain` (plus two static merge helpers, `ActivityLogMerge` and `GranularSeriesMerge`, in `Entities/`), mapped by EF Core (**33** migrations applied as of 2026-08-14 — this count drifts fast and is not re-verified every edit; the pipeline's own output entities, e.g. `RealtimeAssessment`/`DigestEntry`/`EnvironmentalReading`/`MemberQuestionnaire`, are among the 25 but are documented in [llm_design.md](../llm_design.md) instead — `MemberQuestionnaire` also has its own API contract in [questionnaires.md](../execution/backend/api/questionnaires.md), and is the one entity deliberately **not** soft-deletable, since erasing a family's answer has to mean the row is gone). A further set of feature entities is designed but not yet built — see the "Planned" section below.

## Entity Overview

### Core Entities

#### 1. **Organization**
- Represents either a Family account or Business (care home)
- Contains: Name, Type (Family/Business), IsActive
- Guid references only, except the Subscription FK (see Design Principles)

#### 2. **User**
- Login account for family members or care home staff
- Contains: Auth0UserId, Email, PasswordHash, Name, Phone, Role, EmailVerified, LastLoginDate, OrganizationId, Locale, TimeZoneId, HealthDataDisclosureDismissedDate
- Credentials are Auth0-hosted; the `PasswordHash` column (required, max 500) is a legacy artifact pending removal — it is never populated with a real hash
- `Locale`/`TimeZoneId` default `en-US`/`UTC`, derived from the request's Accept-Language
- `HealthDataDisclosureDismissedDate` records dismissal of the Google-required health-data disclosure banner (PR #9)
- Indexes: unique Email, OrganizationId, IsActive, and a **unique FILTERED index on Auth0UserId** (filter: not-empty) that makes onboarding retries race-safe
- Role hidden in UI for Family type organizations

#### 3. **CardiMember**
- Person being monitored (can be the User themselves)
- Contains: Name, Email, Phone, DateOfBirth, Gender, OrganizationId, LastSyncDate
- Emergency contact: two flat columns — EmergencyContactName, EmergencyContactPhone (no JSON, no separate entity yet)
- MedicalNotes: **encrypted at rest** (AES-256-GCM, applied in `CardiMemberService`). Column is `text`, not `varchar(2000)` — ciphertext is longer than the 2000-character input limit
- Monitoring pause: MonitoringPausedUntil (null = monitoring normally) and MonitoringPauseReason. Time-bounded and self-expiring; enforced in `GetDueForSyncAsync`, so a paused member is genuinely not synced
- AlertSensitivity (Low/Medium/High, default Medium) — **stored but consumed by nothing**: alert generation has shipped (see Alert below), yet none of the three producers reads this field
- EnvironmentalContextConsentGranted (bool, default `false`) — the sole gate on the environmental-context enrichment pipeline job (temperature/air-quality lookups for GPS-tagged exercise sessions); see [llm_design.md](../llm_design.md) and [data_protection_architecture.md](./data_protection_architecture.md) §8
- Links to devices, activity logs, alerts, and pattern baselines

#### 4. **UserCardiMember** (Join Table)
- Many-to-many relationship between Users and CardiMembers
- Contains: RelationshipType, IsPrimaryCaregiver, CanViewHealthData, ReceiveAlerts, AssignedDate (the per-relationship `NotificationPreferences` JSON column was dropped by `AddPushDeliverySpine` in favour of the per-User NotificationPreference table)
- Enables multiple users to monitor same CardiMember (care home scenario)

### Device & Health Data Entities

#### 5. **DeviceConnection**
- Stores OAuth tokens for connected wearable devices
- **Device-agnostic design** - supports Fitbit, Apple Watch, Garmin, Samsung, etc.
- Contains: DeviceType, DeviceName, IsPrimary, ConnectionStatus, AccessToken (encrypted AES-256-GCM), RefreshToken (encrypted), TokenExpiry, Scopes (JSON), ConnectedDate, LastSyncDate, Metadata (JSON)
- `SyncFrequencyMinutes` — default **10** (drives the polling sync cycle; reduced from 30 by `ReduceDefaultSyncFrequencyToTenMinutes`)
- `NextPullAt` — when the connection should next be pulled; null falls back to `LastSyncDate + SyncFrequencyMinutes` until cadence calibration writes a schedule
- `HealthUserId` — the provider's user id, used to map inbound webhook notifications to a connection
- `HistoryBackfilledTo` — high-water mark for the walking history backfill; `ConsecutiveEmptyPulls` — schema support for dormancy backoff (not yet consulted)
- `NextAuthRecoveryAt` / `AuthRecoveryAttempts` — widening per-connection backoff state for `DeviceAuthRecoveryWorker`'s retry of TokenExpired/AuthError grants
- `BatteryLevel` / `BatteryStatus` / `BatteryUpdatedAt` — last-known device battery, from the per-pull `pairedDevices` read
- No FK constraints - uses CardiMemberId (Guid)

#### 6. **DeviceActivityLog** *(raw)*
- One day of metrics exactly as a **single device** reported them — **unique on (DeviceConnectionId, Date)**
- Same ~25 nullable metric columns as ActivityLog; indexed on (CardiMemberId, Date) for the merge read
- A CardiMember wearing several devices has one row here **per device per day**
- No FK constraints - uses CardiMemberId and DeviceConnectionId (Guid)

#### 7. **ActivityLog** *(derived)*
- Normalized daily health data for a CardiMember — **unique on (CardiMemberId, Date)**, one row per member per day
- Derived from that member's DeviceActivityLog rows by `ActivityLogMerge`; **every reader consumes this table, not the raw one**
- Merge rule: each metric resolved independently, first non-null wins by device priority (`IsPrimary` desc → `ConnectedDate` asc → `Id`). **Never sums** — two wearables on one body count the same steps. Idempotent, since it always rebuilds from the full raw set
- **Rich metric surface (~27 nullable metrics)**: Steps, Distance, ActiveMinutes, SedentaryMinutes, Floors, CaloriesBurned; Resting/Avg/Max/Min heart rate; sleep duration, start/end, efficiency, and Deep/Light/REM/Awake stage minutes; SpO2 (avg/min/max), VO2Max, StressScore, BreathingRate, Temperature (nightly, plus the wearer's own TemperatureBaseline and TemperatureVariation)
- DataSource / DeviceConnectionId record the highest-priority contributing device
- No FK constraints - uses CardiMemberId and DeviceConnectionId (Guid)

#### 8. **DeviceTypeSyncProfile**
- Observed sync behaviour per **device type** (one row per `DeviceType`): how long after a day ends its data settles (`SettleLatencyP50/P95Hours`), how far back the provider revises (`RevisionTailP99Hours`), and how often a pull finds anything new (`PollYieldRatio`, `SampleSize`)
- Derives `RecommendedPullIntervalMinutes` / `RecommendedLookbackDays`, clamped to per-environment configured bounds — a calibration run can never widen its own limits
- `CalculatedAt` tracks calibration freshness (distinct from `UpdatedDate`)
- Added by migration `AddSyncCadenceProfileAndPullSchedule`

#### 9. **Alert**
- Health alerts — generation has **shipped**: `StatisticalAlertWorker` (statistical rules covering all five types), `InactivityDetectionWorker` (device silence), and the pipeline assessor (HeartRate alerts from MedGemma red/orange verdicts) all create rows
- AlertType: Inactivity, HeartRate, Sleep, PatternBreak, Trend
- AlertSeverity: Green, Yellow, Orange, Red (Green = 1, informational)
- No stored AlertStatus column — lifecycle is tracked with `AcknowledgedDate`, `AcknowledgedByUserId`, and a boolean `IsResolved`; the `AlertStatus` enum (New/Acknowledged/Resolved) is a projection derived from those fields
- MetricValues JSON captures the triggering readings

#### 10. **PatternBaseline**
- AI-learned normal patterns for each CardiMember, recalculated daily by `BaselineCalculationWorker` (02:30 UTC)
- Calculated over 7, 14, 30, 60, and 90 day periods
- Contains: Average and sample σ for steps and resting heart rate, **median and unscaled MAD** for steps, resting HR, and sleep minutes (additive; live alerts still use the mean), sleep averages, typical bedtime/wake, day-of-week variations (JSON)

### Business Entities

#### 11. **Subscription**
- Trial/subscription state per organization — **no billing integration and no Stripe fields**
- Contains: Tier (Basic, Complete, Plus), Status, StartDate, EndDate, `TrialEndDate` (30-day trial), BillingCycle, Price, Currency (default USD), PaymentMethod (JSON), Features (JSON)
- MaxCardiMembers and MaxUsers are **organization-type driven**, not tier driven: Family 5 members / 1 user; Business 50 / 20
- Unique index on OrganizationId; **FK to Organizations with cascade delete** (the one FK in the schema)

#### 12. **Device** (Catalog)
- Reference data for supported wearable devices
- Contains: DeviceType, Manufacturer, ModelName, DisplayName, Capabilities (JSON), ApiEndpoint, OAuthConfig (JSON), SortOrder, IconUrl
- Used for UI display and capability checking; catalog `DisplayName` takes precedence over the enum display name

### Compliance Entities

#### 13. **AuditLog**
- HIPAA compliance audit trail for PHI access
- Contains: UserId, CardiMemberId, Action, EntityType, Timestamp, IP address, user agent, request details, DataAccessed/ChangedFields (JSON)
- **Retention policy is 6 years**; infrastructure currently implements **30 days dev / 90 days prod** (tfvars) — closing that gap is tracked follow-up infra work
- Written by `AuditLoggingMiddleware` (in `CardiTrack.API`) via `IAuditLogRepository` — opt-in per endpoint through `AuditHealthDataAccessAttribute`, carried by eight controllers: controller-wide on CardiMembers, Dashboard, Devices, Insights, Chat, Reports, and Questionnaires, plus per-action on Alerts; Auth, Onboarding, and Notifications are not annotated

## Planned Entities — not yet implemented

> **Status: Planned — not yet implemented.** The entities below back designed API features (see [/execution/backend/api/](../execution/backend/api/readme.md)) but have no classes, tables, or migrations today. Where a slice of the capability already exists in another shape, it is noted.

- **EmergencyContact** — up to 5 per CardiMember (name, phone, relationship). *Today: two flat columns on CardiMember (EmergencyContactName/Phone).*
- **ConsentRecord** — append-only per-metric consent history; latest row is current
- **FamilyInvitation** — email invitations with role, 7-day expiry, Pending/Accepted/Revoked/Expired status
- **SharedNote** — care-coordination notes per CardiMember with @mentions (JSON) and view receipts (JSON)
- **CardiMemberNote** — self-authored notes by the monitored person (max 1000 chars)
- **AlertNote** — follow-up notes on an alert, with optional actionTaken analytics key
- **AlertPhoto** — photo attachments on alerts (blob URL, caption)
- **AlertPreference** — one per CardiMember: sparse JSON disable-list of alert rule ids (`DisabledRules`). Missing row = all rules on. Producers skip evaluation for disabled ids.
- ~~**PushNotificationToken**~~ — **shipped** as `PushDeviceToken` (APNS/FCM tokens per user device, token encrypted with a SHA-256 fingerprint for lookup), part of the push delivery spine (with Notification, NotificationDelivery, NotificationMute)
- ~~**NotificationPreference**~~ — **shipped**: a per-User NotificationPreference table (the old per-relationship `NotificationPreferences` JSON column on UserCardiMember was dropped by `AddPushDeliverySpine`)
- **Report** — async report generation state (format, parameters, status, download expiry). *Today: report state lives in the distributed cache only (fire-and-forget, lost on restart) — no entity or table.*

> **Biometric credentials have no entity** — biometrics are a local device gate over the Auth0 refresh token (see [auth.md](../execution/backend/api/auth.md)).

## Design Principles

### 1. Minimal Foreign Key Constraints
- Relationships use Guid references without FK constraints, **with one exception: Subscriptions → Organizations (cascade delete)**, added Aug 2026 so a subscription can never outlive its organization
- Application-level referential integrity via repositories elsewhere
- More flexible for soft deletes and data archival

### 2. Guid Primary Keys
- All entities use Guid for Id (not int)
- Better for distributed systems
- No sequential ID enumeration security risk
- Easier cross-database/cross-service references

### 3. Device-Agnostic Architecture
- DeviceType enum supports all wearables (Fitbit, Apple Watch, Garmin, Samsung Galaxy Watch, Withings, Oura, Whoop, Google Pixel Watch)
- ActivityLog.DataSource tracks which device provided data
- Normalized data schema works with any device
- Device catalog table for device capabilities

### 4. Soft Deletes
- `ISoftDeletable` (IsActive flag) applies to **Organization, User, CardiMember, UserCardiMember, DeviceConnection, Alert, Device, and Notification** only — ActivityLog, PatternBaseline, Subscription, and AuditLog are not soft-deletable
- Maintains data integrity and audit trail
- HIPAA compliance for data retention

### 5. JSON for Flexibility
- Metadata, Features, PaymentMethod, Scopes, Capabilities stored as JSON
- Allows schema evolution without migrations
- Pattern baselines store day-of-week arrays

### 6. Security & Encryption
- Device OAuth tokens (AccessToken, RefreshToken) and CardiMember MedicalNotes are encrypted with AES-256-GCM — see [data_protection_architecture.md](./data_protection_architecture.md)
- Credentials are Auth0-hosted; a legacy `PasswordHash` column remains on Users pending removal
- Audit logging is wired via `AuditLoggingMiddleware`, opt-in per endpoint through `AuditHealthDataAccessAttribute` (health-data controllers only; onboarding writes are not yet audited)

## Entity Relationships

```
Organization (1) ──→ (N) User
Organization (1) ──→ (N) CardiMember
Organization (1) ──→ (1) Subscription   [FK, cascade delete]

User (M) ←──→ (N) CardiMember (via UserCardiMember join table)

CardiMember (1) ──→ (N) DeviceConnection
DeviceConnection (1) ──→ (N) DeviceActivityLog   [raw: one per device per day]
CardiMember (1) ──→ (N) ActivityLog              [derived: one per member per day]
CardiMember (1) ──→ (N) Alert
CardiMember (1) ──→ (N) PatternBaseline

DeviceConnection (1) ──→ (N) ActivityLog
User (1) ──→ (N) AuditLog

DeviceConnection (1) ──→ (N) GranularMetricHour   [per device × metric × hour]
CardiMember (1) ──→ (N) MetricRollupHourly        [per member × metric × hour]
CardiMember (1) ──→ (N) RealtimeAssessment
CardiMember (1) ──→ (N) DigestEntry
CardiMember (1) ──→ (N) EnvironmentalReading
CardiMember (1) ──→ (N) MemberQuestionnaire
User (1) ──→ (N) Notification / NotificationDelivery / PushDeviceToken / NotificationMute
User (1) ──→ (1) NotificationPreference
```

`DeviceTypeSyncProfile` stands alone — one row per `DeviceType` value, no relationships to other entities.

Planned relationships (when the planned entities land): Organization→FamilyInvitation, User→Report, CardiMember→EmergencyContact/ConsentRecord/SharedNote/CardiMemberNote/AlertPreference, Alert→AlertNote/AlertPhoto.

## Enums

The 32 domain enums:

- **OrganizationType**: Family, Business
- **UserRole**: Member, Admin, Staff (displays "Member" / "Administrator" / "Staff Member")
- **Gender**: Male (1), Female (2), PreferNotToSay (4) — Other (= 3) retired by `RetireOtherGender`, value reserved
- **RelationshipType**: Self, Parent, Spouse, Grandparent, Sibling, Child, Other (= 99)
- **DeviceType**: Fitbit, AppleWatch, Garmin, GalaxyWatch (displays "Samsung Galaxy Watch"), Withings, Oura, Whoop, GooglePixelWatch (= 8), Other (= 99)
- **DevicePlatform**: Ios, Android (the phone the app runs on, for push tokens — not the wearable)
- **ConnectionStatus**: Connected, Disconnected, TokenExpired, AuthError, SyncError (no Pending)
- **HealthApi**: GoogleHealth, SamsungHealth, GarminConnect, AppleHealth, Withings, Oura, Whoop (which provider API serves a device type)
- **GranularMetric**: HeartRate, Steps, ActiveZoneMinutes, SpO2 (the four minute-grain series)
- **AlertType**: Inactivity, HeartRate, Sleep, PatternBreak, Trend
- **AlertSeverity**: Green (1), Yellow, Orange, Red
- **AlertSensitivity**: Low, Medium, High (default Medium on CardiMember; stored, consumed by nothing)
- **AlertStatus**: New, Acknowledged, Resolved (derived projection over Alert lifecycle fields — not a stored column)
- **NotificationCategory**: Safety, Blocking, Unlock, Account
- **NotificationPriority**: Critical, High, Medium, Low
- **NotificationState**: Open, Snoozed, Resolved, Superseded
- **NotificationResolutionReason**: GapClosed, Dismissed, MonitoringPaused, ScopeRemoved
- **DeliveryCategory**: Safety, Health, Nudge (delivery policy class for outbound pushes)
- **DeliveryChannel**: Push, InApp
- **DeliverySourceType**: Alert, Notification
- **DeliveryState**: Pending, Sent, Delivered, Suppressed, Failed, DeadLettered, Undelivered
- **EscalationStage**: Initial, Repushed, FannedOut, UndeliveredCritical
- **OsAuthorizationStatus**: NotDetermined, Denied, Granted, Provisional, Ephemeral (OS-level push permission)
- **DigestAudience**: Family, Wearer, Daybook (Wearer generated only once wearer logins exist — currently never. Daybook is the once-daily account of a finished day: same family reader as Family, different reading-mode — see [llm_design.md](../llm_design.md))
- **QuestionnaireStatus**: Pending, Answered, Dismissed
- **QuestionnaireScope**: TimeScoped (1), Permanent (2) — standing facts stay in every future prompt until deleted; momentary ones age out
- **DigestUrgency**: Watch (1), CheckIn, Concerning, ActNow — the model's read of how soon the family should act on a digest; never drives Alert rows
- **SubscriptionTier**: Basic, Complete, Plus
- **SubscriptionStatus**: Trial (1), Active, PastDue, Cancelled, Suspended
- **BillingCycle**: Monthly, Annual
- **ReportFormat**: Pdf, Csv, FhirR4, Hl7V2
- **ReportStatus**: Pending, Ready, Failed, Expired

There are no IntegrationMode, HealthStatus, or InvitationStatus enums.

> API surfaces serialize enum values as **integers** (no string-enum converter is registered) — e.g. `"severity": 2` for Yellow. The PascalCase names above are the C# domain enums; display names come from `[Display]` attributes server-side (see [enum_extensions_guide.md](./enum_extensions_guide.md)).

## File Structure

```
CardiTrack.Domain/
├── Common/
│   └── BaseEntity.cs
├── Interfaces/
│   ├── IEntity.cs
│   └── ISoftDeletable.cs
├── Enums/       one file per enum — the 32 listed above
└── Entities/    27 files — the 25 entity classes, plus the two static
                 merge helpers (ActivityLogMerge.cs, GranularSeriesMerge.cs)
```

EF Core mapping lives in `CardiTrack.Infrastructure/Persistence` (a configuration class per entity; plural table names — Users, CardiMembers, ActivityLogs, PatternBaselines, Alerts, AuditLogs, ...). 33 migrations exist — see `Migrations/` for the current list (the latest: `AddRobustBaselineStatsAndSsaEngine`, `AddNotificationPushedDate`, `AddDigestUrgency`, `AddQuestionnaireScope`, `DigestSuggestionToSingleMessage`).

## Next Steps

1. ~~Create EF Core DbContext and entity configurations~~ — done (`CardiTrackDbContext` + per-entity FluentAPI configurations, 33 migrations)
2. ~~Set up encryption for device OAuth tokens and MedicalNotes~~ — done (AES-256-GCM for both)
3. ~~Implement repositories with Guid-based queries~~ — done (UnitOfWork + repositories)
4. ~~Add core indexes~~ — done (unique Email, filtered unique Auth0UserId, OrganizationId, status indexes)
5. ~~Wire audit-logging middleware so AuditLogs actually receives writes~~ — done (`AuditLoggingMiddleware`)
6. Remove the legacy `Users.PasswordHash` column
7. Create migrations for the planned feature entities when their features are scheduled
8. Persist Report state (currently cache-only, lost on restart)
9. Extend audit-log retention infrastructure from 30/90 days to the 6-year policy

---

**Last Updated:** August 14, 2026
