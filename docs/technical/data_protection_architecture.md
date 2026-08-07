# Data Protection Architecture — HIPAA / GDPR Retention & De-Identification

**Status:** Proposed (ADR — awaiting engineering review and the legal/compliance decisions in [§10](#10-decisions-required-from-legalcompliance-not-engineering))
**Scope:** Classification, pseudonymization, Safe Harbor de-identification, retention/deletion, access controls, consent, and subprocessor obligations for all medical/health data in CardiTrack.
**Relationship to other docs:** [infrastructure.md](../infrastructure.md) describes the deployed PostgreSQL (Cloud SQL) on GCP data model per `infrastructure/` Terraform. This ADR designs against the deployed system and notes where the planned AI pipeline ([llm_design.md](../llm_design.md)) must follow the same rules.
> **Platform note (Aug 7, 2026):** the AI pipeline design has been re-platformed from Azure to GCP (Pub/Sub + Cloud Run, with pipeline outputs as PostgreSQL JSONB tables — see [llm_design.md](../llm_design.md)). References to Azure services and Cosmos DB collections below describe the superseded design; the retention/TTL, consent, and erasure controls carry over unchanged to their GCP equivalents.

---

## Table of Contents

1. [Current state — what exists and what is broken](#1-current-state)
2. [Data classification model](#2-data-classification-model)
3. [Target schema — identity/clinical separation](#3-target-schema)
4. [De-identification pipeline](#4-de-identification-pipeline)
5. [Retention & deletion](#5-retention--deletion)
6. [GDPR erasure pipeline](#6-gdpr-erasure-pipeline)
7. [Access & security controls](#7-access--security-controls)
8. [Consent & lawful basis model](#8-consent--lawful-basis-model)
9. [Subprocessor register](#9-subprocessor-register)
10. [Decisions required from legal/compliance](#10-decisions-required-from-legalcompliance-not-engineering)
11. [Implementation phases](#11-implementation-phases)

---

## 1. Current state

What the code actually does today (verified against source, not docs). Items marked **[GAP]** are prerequisites this ADR builds on; items marked **[BUG]** are defects to fix regardless of the rest of the design.

| # | Finding | Evidence |
|---|---------|----------|
| 1 | **[GAP]** Identifiers and clinical payload live in one table: `CardiMembers` holds Name, Email, Phone, full DOB, Gender, emergency contacts *and* `MedicalNotes` | `src/Core/CardiTrack.Domain/Entities/CardiMember.cs:10-19` |
| 2 | **[BUG]** `MedicalNotes` is stored **in plaintext** despite comments claiming encryption — the encrypt call was never written | `src/Core/CardiTrack.Domain/Entities/CardiMember.cs:17` ("Encrypted in database"), `src/Core/CardiTrack.Application/Services/CardiMemberService.cs:33` (`// TODO: Encrypt this`), config comment at `src/Infrastructure/CardiTrack.Infrastructure/Persistence/Configurations/CardiMemberConfiguration.cs:42-44` |
| 3 | **[GAP]** `AuditLogs` table exists with HIPAA-labelled indexes but **nothing ever writes to it** — zero PHI access is audited | Entity `src/Core/CardiTrack.Domain/Entities/AuditLog.cs`, config `src/Infrastructure/CardiTrack.Infrastructure/Persistence/Configurations/AuditLogConfiguration.cs:66-72`; no repository/interceptor/middleware references it anywhere in `src/` |
| 4 | **[GAP]** The `ConsentRecord` entity is specified in docs but **not built**; the only consent-shaped data is `UserCardiMember.CanViewHealthData`/`ReceiveAlerts` (mutable booleans, no history) and a banner-dismissal timestamp | Spec: [entity_summary.md](./entity_summary.md) §13, [infrastructure.md](../infrastructure.md) `ConsentRecords` DDL; flags at `src/Core/CardiTrack.Domain/Entities/UserCardiMember.cs:13-14`; `User.HealthDataDisclosureDismissedDate` at `src/Core/CardiTrack.Domain/Entities/User.cs:22` |
| 5 | **[GAP]** **No retention job, no erasure path, no DELETE endpoint anywhere.** Worker hosts only `WearableSyncWorker` (accretes one `ActivityLogs` row/member/day, never pruned) and `OrphanedOrganizationCleanupWorker` (signup garbage only, touches zero PHI) | `src/Worker/CardiTrack.Worker/Workers/WearableSyncWorker.cs`, `src/Worker/CardiTrack.Worker/Workers/OrphanedOrganizationCleanupWorker.cs`; all 18 API endpoints are GET/POST |
| 6 | **[GAP]** Almost **no foreign keys** (deliberate — [entity_summary.md](./entity_summary.md) "Design Principles"). Only `UserCardiMembers` and `Subscriptions` cascade. Deleting a CardiMember silently orphans all `ActivityLogs`, `Alerts`, `PatternBaselines`, `DeviceConnections`, `AuditLogs` rows — they stay live and queryable (those tables don't implement `ISoftDeletable`, and there is no global query filter) | `src/Infrastructure/CardiTrack.Infrastructure/Migrations/20260312180945_InitialCreate.cs` (one `table.ForeignKey`); `src/Infrastructure/CardiTrack.Infrastructure/Persistence/CardiTrackDbContext.cs:29-35` (no `HasQueryFilter`) |
| 7 | **[BUG]** **PHI leaves the estate identifiable:** report generation concatenates the wearer's real name plus day-by-day readings into a Gemini prompt (`generativelanguage.googleapis.com` — *not* covered by a Google Cloud BAA) | `src/Infrastructure/CardiTrack.Infrastructure/Services/ReportGenerationService.cs:148` (`## Patient: {member.Name}`), `:154`, `:167`; client `ExternalClients/General/GeminiClient.cs:29` (API key in query string) |
| 8 | **[BUG]** Report cache has no ownership check — any authenticated caller with a report ID can download another family's report | `ReportGenerationService.cs:67,73` (`requestingUserId` accepted, never compared) |
| 9 | Encryption service is sound (AES-256-GCM) but has a **single static key with no key ID in the ciphertext** — no rotation path, and crypto-shredding-based erasure is not currently expressible. Only OAuth tokens use it | `src/Infrastructure/CardiTrack.Infrastructure/Security/AesEncryptionService.cs:63-67`; consumers: `Services/DeviceConnectionService.cs:205-206`, `ExternalClients/OAuthTokenRefreshService.cs:36-95` |
| 10 | Telemetry ships request paths containing `{cardiMemberId}` GUIDs, exception payloads, and Npgsql spans to Datadog/Better Stack with no scrubbing or retention config; **mobile RUM hardcodes `TrackingConsent.Granted` at 100% sampling** | `src/Infrastructure/CardiTrack.Observability/ApmExtensions.cs:87-151`, `src/Presentation/CardiTrack.Mobile/Services/MobileApm.cs:71,92` |
| 11 | Backups/versioning defeat naive deletion claims: Cloud SQL `retained_backups = 7`, GCS bucket versioning on | `infrastructure/deployments/cloud_sql.tf:87-93`, `deployments/cloud_storage.tf:44-46` |
| 12 | `CronBackgroundService` has no distributed lock and no error boundary — unsafe for destructive jobs at `cloud_run_max_instances = 3` | `src/Worker/CardiTrack.Worker/CronBackgroundService.cs:16-33`, `infrastructure/main.tf:46` |
| 13 | Dead/misleading columns: `User.PasswordHash` is required-non-null but never read or written (auth is Auth0-hosted). *(The unused `Encryption:IV` config key has since been removed.)* | `Configurations/UserConfiguration.cs:22-24` |

> The Web Data Protection key ring on GCS (antiforgery only) is a previously **accepted risk** and out of scope here.

Everything in §§3–8 below is **net-new** unless a file reference says otherwise.

---

## 2. Data classification model

Three tiers, with tier membership decided **per column**, not per table. The tier determines storage location, encryption, access path, audit requirements, and retention rules.

### Tier 1 — Direct identifiers (PII vault)

Data that identifies a person on its own. HIPAA Safe Harbor categories map here.

| Data | Today lives in | Moves to |
|------|----------------|----------|
| Wearer name, email, phone | `CardiMembers` | `pii.subject_identities` (§3) |
| Full date of birth | `CardiMembers.DateOfBirth` | `pii.subject_identities`; clinical plane keeps only `BirthYear` (+ "90+" bucket) |
| Emergency contact name/phone | `CardiMembers` | `pii.subject_identities` |
| Medical notes (free text) | `CardiMembers.MedicalNotes` (plaintext!) | `pii.subject_identities`, encrypted with the subject DEK |
| Caregiver name, email, phone, Auth0UserId | `Users` | stays in `Users` (account data, not PHI — but in GDPR erasure scope) |
| Device OAuth tokens | `DeviceConnections` (encrypted) | stays; re-keyed to versioned per-subject DEK (§7) |
| Device labels ("Mom's Fitbit"), consent signatory names | `DeviceConnections.DeviceName`, future `ConsentRecords` | treat as identifier-bearing free text: never exported, never sent to LLMs |
| IP address, user agent | `AuditLogs` | stays (audit plane; exempt from erasure — §6) |
| Push tokens (planned), Stripe IDs (planned) | — | Tier 1 on arrival |

### Tier 2 — Pseudonymized clinical plane

Health payload keyed only by `CardiMemberId` (a random GUID that carries no identity by itself). This is where `ActivityLogs`, `Alerts`, `PatternBaselines`, `DeviceConnections` (minus label), and the planned Cosmos collections live.

**Explicitly: Tier 2 is still PHI under HIPAA and still personal data under GDPR** (Art. 4(5) — pseudonymized data with a retained re-linking capability is personal data). The tier split does not shrink compliance scope. It changes the blast radius: a leaked clinical table exposes readings for anonymous GUIDs; a leaked PII vault exposes identities without readings; only a joint compromise plus vault decryption exposes both.

### Tier 3 — De-identified analytics/export plane

Output of the Safe Harbor transform (§4), keyed by a non-reversible `AnalyticsId`. Only Tier 3 data may be used for product analytics, model benchmarking, or any export that doesn't serve the individual data subject. HIPAA no longer applies to conforming Safe Harbor output; whether GDPR still applies depends on our residual re-identification means — treat Tier 3 as GDPR-anonymous **only after** the quasi-identifier controls in §4.3 are applied and legal signs off (§10, D7).

**Rule: raw free text (medical notes, alert messages, notes, device names) never crosses into Tier 3.** Free text cannot be reliably de-identified by field policy; it is excluded from every export.

---

## 3. Target schema

### 3.1 Layout

Postgres schemas give the physical separation with per-role grants — no second database needed at current scale.

```
┌─────────────────────────────────────────────────────────────────────────┐
│ PostgreSQL (Cloud SQL, private VPC, TDE at rest)                        │
│                                                                         │
│  schema: pii            schema: clinical (= current public, renamed     │
│  ┌───────────────────┐            conceptually; migration renames or   │
│  │ subject_identities│            leaves in public with grants)        │
│  │ user_pii (opt.)   │  ┌──────────────┬──────────┬─────────────────┐  │
│  └───────────────────┘  │ activity_logs│ alerts   │ pattern_baselines│  │
│   role: identity_svc    │ device_conns │ subjects │ …               │  │
│   (only)                └──────────────┴──────────┴─────────────────┘  │
│                          role: app_rw (NO grant on pii.*)              │
│  schema: compliance                                                     │
│  ┌──────────────┬────────────────┬─────────────────┬────────────────┐  │
│  │ audit_logs   │ consent_records│ erasure_requests│ retention_runs │  │
│  └──────────────┴────────────────┴─────────────────┴────────────────┘  │
│   role: app_append (INSERT only) + compliance_ro (SELECT)              │
└─────────────────────────────────────────────────────────────────────────┘
         ▲                                    ▲
         │ IdentityVaultService               │ repositories (existing)
         │ (own DbContext, own connection     │ role: app_rw
         │  string / DB role: identity_svc;   │
         │  DEKs unwrapped via Cloud KMS —    │
         │  KMS IAM: identity SA only)        │
```

### 3.2 `pii.subject_identities` (net-new)

One row per monitored person. The **re-linking key store**: possession of `CardiMemberId` plus this table plus KMS decrypt rights is what re-identifies Tier 2 data.

```sql
CREATE SCHEMA IF NOT EXISTS pii;

CREATE TABLE pii.subject_identities (
    cardi_member_id   uuid PRIMARY KEY,           -- = clinical.subjects key (the pseudonym)
    payload_ciphertext bytea NOT NULL,            -- AES-256-GCM over the identity JSON:
                                                  -- {name, email, phone, dateOfBirth, gender,
                                                  --  emergencyContacts[], medicalNotes}
    dek_wrapped       bytea NOT NULL,             -- per-subject data key, wrapped by Cloud KMS KEK
    kek_version       text  NOT NULL,             -- KMS key version used to wrap
    created_at        timestamptz NOT NULL DEFAULT now(),
    updated_at        timestamptz,
    shredded_at       timestamptz                 -- crypto-shred tombstone (dek_wrapped nulled)
);

REVOKE ALL ON SCHEMA pii FROM PUBLIC;
GRANT USAGE ON SCHEMA pii TO identity_svc;
GRANT SELECT, INSERT, UPDATE ON pii.subject_identities TO identity_svc;
-- app_rw (API/Worker general role) gets NO grant here.
```

`CardiMembers` (clinical plane) is then slimmed to the non-identifying operational core:

```sql
-- After migration, clinical CardiMembers row:
--   Id, OrganizationId, BirthYear int, IsOver89 bit, Gender,
--   LastSyncDate, MonitoringPausedUntil, IsActive, CreatedDate, UpdatedDate
-- Dropped from clinical: Name, Email, Phone, DateOfBirth,
--   EmergencyContactName/Phone, MedicalNotes  → pii.subject_identities
```

Application code path: dashboards that need "Margaret, 78 — HR 72" call `IIdentityVault.GetDisplayIdentityAsync(cardiMemberId)` (name only, cached ≤5 min, every call audited) and join in memory. List/aggregate screens use `BirthYear`-derived age. Nothing in the API/Worker composes SQL joins across the schemas — the grants make it impossible.

**Why per-subject DEKs:** (a) crypto-shredding — GDPR erasure destroys `dek_wrapped` and the payload is gone even in the 7-day backup window; (b) a stolen DB dump without KMS access yields nothing from the vault; (c) key rotation is re-wrapping DEKs, not re-encrypting payloads.

### 3.3 `Users` (caregivers)

Caregiver PII stays in `Users` — it's account data needed on nearly every request, and it is *not* the PHI subject's identity. It remains GDPR personal data (in erasure scope, §6) and gets: drop the dead `PasswordHash` column ([§1.13](#1-current-state)), audit on reads of other users' rows, and inclusion in the DSAR export.

*Optional hardening (Phase 3):* move caregiver email/phone to a `pii.user_pii` table under the same vault pattern if the threat model warrants it.

### 3.4 Referential integrity for erasure

Keep the "no FK constraints" principle for the clinical plane if desired, but make the subject-ownership graph **explicit and testable** instead of implied:

```csharp
/// Compile-time registry of every table owning subject-linked rows.
/// The erasure job, retention job, and DSAR export ALL iterate this list —
/// a new entity referencing CardiMemberId or UserId MUST be added here
/// (enforced by the architecture test below).
public static class SubjectDataMap
{
    public static readonly SubjectTable[] ByCardiMember =
    {
        new(nameof(ActivityLog),      "activity_logs",      "cardi_member_id"),
        new(nameof(Alert),            "alerts",             "cardi_member_id"),
        new(nameof(PatternBaseline),  "pattern_baselines",  "cardi_member_id"),
        new(nameof(DeviceConnection), "device_connections", "cardi_member_id"),
        new(nameof(AuditLog),         "audit_logs",         "cardi_member_id", Erasable: false), // legal hold, §6
        // planned: consent_records (Erasable: false), Cosmos collections by partition key
    };

    public static readonly SubjectTable[] ByUser =
    {
        new(nameof(Alert),    "alerts",     "acknowledged_by_user_id", Mode: ErasureMode.NullOut),
        new(nameof(AuditLog), "audit_logs", "user_id", Erasable: false),
    };
}

// tests/CardiTrack.UnitTests/Architecture/SubjectDataMapTests.cs
// Reflect over CardiTrack.Domain: any entity with a property named
// CardiMemberId or UserId must appear in SubjectDataMap → fails the build
// when someone adds a new PHI table and forgets erasure/retention coverage.
```

---

## 4. De-identification pipeline

### 4.1 Two distinct outputs — don't conflate them

| Output | Mechanism | HIPAA status | GDPR status |
|--------|-----------|--------------|-------------|
| **Operational pseudonymization** (Tier 2) | Identity split into `pii` vault; clinical keyed by GUID | Still PHI | Still personal data (Art. 4(5)) — full GDPR obligations remain |
| **Safe Harbor export** (Tier 3) | §4.2 transform, all 18 categories removed | De-identified (45 CFR §164.514(b)(2)) | Anonymous only if §4.3 controls hold and we don't retain practical re-identification means — legal call (D7) |

A day-level time series **cannot** be Safe Harbor output (category 3 forbids dates finer than year). Analytics that need daily granularity must either stay Tier 2 (full compliance scope) or go through **Expert Determination** (§164.514(b)(1)) with date-shifting — that path needs a hired expert and is a legal/budget decision (D8), not something engineering can self-certify.

### 4.2 Safe Harbor transform — explicit, testable, fails closed

All 18 §164.514(b)(2) categories, mapped to CardiTrack fields:

| # | HIPAA category | CardiTrack field(s) | Action |
|---|----------------|--------------------|--------|
| 1 | Names | `pii` vault name, emergency contacts, `ConsentedByName`, `DeviceConnection.DeviceName`, `Organization.Name` (family surname!) | **Strip** (never enters export input) |
| 2 | Geographic subdivisions < state | None stored today. `User.Locale`/`TimeZoneId` are proxies | **Generalize**: timezone → country-level UTC offset band. If address/ZIP is ever added: first 3 ZIP digits only where the 3-digit area population > 20,000, else `000` (the §164.514(b)(2)(i)(B) carve-out) — encode the current census list in config, not code |
| 3 | All date elements < year (incl. DOB, admission/service dates); ages > 89 | `DateOfBirth`; `ActivityLogs.Date`, `SleepStartTime/EndTime`; `Alert.TriggeredDate`; `PatternBaseline.CalculatedDate`, `TypicalBedtime/WakeTime` | **Generalize**: DOB → year; age > 89 → `90+`; reading dates → year (or month-of-year *count* aggregates); absolute sleep timestamps → durations only. Clock-time-of-day fields (`TypicalBedtime`) are quasi-identifiers → §4.3 |
| 4 | Telephone numbers | vault | Strip |
| 5 | Fax numbers | n/a | — |
| 6 | Email addresses | vault, `FamilyInvitations.Email` | Strip |
| 7 | SSNs | n/a | — |
| 8 | Medical record numbers | n/a (flag if EHR integration ever lands) | — |
| 9 | Health-plan beneficiary numbers | n/a | — |
| 10 | Account numbers | Stripe customer/subscription IDs (planned), Subscription.Id | Strip |
| 11 | Certificate/license numbers | n/a | — |
| 12 | Vehicle identifiers | n/a | — |
| 13 | **Device identifiers & serial numbers** | `DeviceConnection.Id`, `DeviceUserId` (Google account-scoped ID), device `Metadata` JSON (model + firmware), push tokens | **Strip**. Keep only coarse `DeviceType` enum (Fitbit/AppleWatch/…) |
| 14 | URLs | `AlertPhoto.BlobUrl` (planned), `Report.BlobUrl`, audit `RequestPath` | Strip |
| 15 | IP addresses | `AuditLogs.IpAddress` | Strip (audit rows are never export input anyway) |
| 16 | Biometric identifiers | No fingerprints/voiceprints stored. High-resolution HR/HRV streams are arguably biometric-adjacent → treated under #18/§4.3 | — |
| 17 | Full-face photos & comparable images | `AlertPhotos` (planned) | Strip |
| 18 | Any other unique identifying number/characteristic/code | `CardiMemberId`, `Auth0UserId`, `OrganizationId` | **Replace** with `AnalyticsId` (below); strip the rest. Free text categorically excluded |

**Re-identification code (§164.514(c)):** the export key is
`AnalyticsId = HMAC-SHA256(CardiMemberId, export_salt)` with `export_salt` held only in Cloud KMS/Secret Manager, IAM-granted to the export job SA — **not** to the API or Worker general roles. This satisfies §164.514(c): not derived from patient identifiers (input is a random GUID), and the means of re-identification (salt) is not disclosed alongside the data.

**Implementation — pure function + fail-closed policy:**

```csharp
public sealed record DeidentifiedDailyRecord(
    string AnalyticsId, int Year, string AgeBand, string Gender, string DeviceType,
    int? Steps, int? RestingHeartRate, int? AvgHeartRate, int? HrvAverage,
    int? SleepMinutes, int? SleepEfficiency, decimal? SpO2Average /* …metrics only */);

public sealed class SafeHarborDeidentifier
{
    // Every source property gets an EXPLICIT verdict. There is no default.
    private static readonly IReadOnlyDictionary<string, FieldPolicy> Policy = new Dictionary<string, FieldPolicy>
    {
        [nameof(ActivityLog.CardiMemberId)]     = FieldPolicy.ReplaceWithAnalyticsId,
        [nameof(ActivityLog.Date)]              = FieldPolicy.GeneralizeToYear,
        [nameof(ActivityLog.SleepStartTime)]    = FieldPolicy.Strip,   // absolute timestamp
        [nameof(ActivityLog.SleepEndTime)]      = FieldPolicy.Strip,
        [nameof(ActivityLog.DeviceConnectionId)]= FieldPolicy.Strip,   // category 13
        [nameof(ActivityLog.DataSource)]        = FieldPolicy.Allow,   // coarse enum
        [nameof(ActivityLog.Steps)]             = FieldPolicy.Allow,
        [nameof(ActivityLog.RestingHeartRate)]  = FieldPolicy.Allow,
        // … every remaining property listed explicitly …
    };

    public DeidentifiedDailyRecord Deidentify(ActivityLog log, SubjectFacts facts, IAnalyticsIdProvider ids)
    {
        // facts = {BirthYear, IsOver89, Gender} from the clinical plane — never touches the pii vault.
        return new DeidentifiedDailyRecord(
            AnalyticsId: ids.For(log.CardiMemberId),                  // HMAC via KMS-held salt
            Year:        log.Date.Year,
            AgeBand:     AgeBands.From(facts.BirthYear, facts.IsOver89, log.Date.Year), // "70-74", "90+"
            Gender:      facts.Gender.ToString(),
            DeviceType:  log.DataSource.ToString(),
            Steps: log.Steps, RestingHeartRate: log.RestingHeartRate, /* … */);
    }
}

// Fail-closed guard (unit test):
//   Reflect over ActivityLog / Alert / PatternBaseline public properties;
//   assert each has an entry in Policy. Adding a column without a de-id
//   verdict breaks the build — new fields can never leak by omission.
// Plus golden tests: a synthetic subject with every field populated goes in;
//   assert the output contains no GUID, no date finer than year, no string
//   from the identifier corpus (names/emails/phones planted in the fixture).
```

### 4.3 Quasi-identifier risk — cardiac data specifics

Safe Harbor field-stripping is necessary but not sufficient here. Concrete re-identification vectors in this dataset:

- **Rare combinations:** `(BirthYear, Gender, DeviceType, Organization size)` — a 94-year-old man in a 3-person family org with a Whoop is likely unique even with no name attached.
- **Behavioural fingerprints:** `TypicalBedtime`/`TypicalWakeTime` and `StepsByDayOfWeek` are stable per-person patterns; joined with any external data (a care home's shift logs, social posts) they can single a person out.
- **Extreme clinical values:** a resting HR of 38 or a documented 3-day cardiac-event pattern narrows candidates sharply in a small cohort. CardiTrack's elderly-cardiac niche makes cohorts *small by construction*.
- **Longitudinal linkage:** a consistent `AnalyticsId` across years lets an attacker accumulate a fingerprint. Rotate `export_salt` per export batch unless longitudinal analysis is explicitly required and risk-accepted (D9).

**Controls applied to every Tier 3 dataset (`KAnonymityGate`, runs after the field transform):**

1. Quasi-identifier set: `{AgeBand, Gender, DeviceType, Year, UtcOffsetBand}`.
2. Compute equivalence-class sizes; **suppress or further generalize any class with k < 5** (5 is the working default — threshold ratification is D9). Generalization ladder: 5-year age band → 10-year → "65+"; drop `UtcOffsetBand`; drop `DeviceType`.
3. Winsorize extreme physiological values to the 1st/99th percentile of the cohort (rare values are identifying).
4. Bucket behavioural times to the hour, or export only variance/regularity scores, never the clock times.
5. Every export writes a manifest row (`compliance.export_manifests`): dataset hash, row count, suppressed-class count, salt version, policy version — the audit trail that a given export was gated.

---

## 5. Retention & deletion

### 5.1 Retention matrix

Periods marked ⚖ are engineering **proposals** requiring legal ratification (D2) — the mechanism is built regardless; the numbers are config.

| Category | Store | Retention | End-of-life action | Rationale |
|----------|-------|-----------|--------------------|-----------|
| Raw daily readings (`ActivityLogs`) | Postgres clinical | ⚖ 25 months rolling | **Hard delete** (batched `ExecuteDelete`), after folding into de-identified monthly aggregates (§4) | 2 years covers YoY trend UX ([infrastructure.md](../infrastructure.md) archival note); raw grain not needed beyond |
| Derived baselines (`PatternBaselines`) | Postgres clinical | ⚖ 12 months (keep latest per period regardless) | Hard delete — fully regenerable | Derived data |
| Alerts + notes/photos | Postgres clinical / GCS | ⚖ 24 months after resolution | **Anonymize-in-place**: null `AcknowledgedBy`/`ResolvedBy`, replace `Title`/`Message`/`MetricValues` with type+severity codes; delete photos (blobs + rows). Row skeleton retained for alert-quality stats | Free text + user refs are the risk; counts are the value |
| Device connections + OAuth tokens | Postgres clinical | Life of connection; **tokens purged ≤ 24 h after disconnect/consent-withdrawal**, with provider-side revoke (`https://oauth2.googleapis.com/revoke`) | Hard delete of token columns; connection row hard-deleted at member erasure | Live credentials to third-party PHI |
| Audit logs (`compliance.audit_logs`) | Postgres → GCS archive | **6 years** (HIPAA §164.316(b)(2)(i)); 1 year hot, then export to a **bucket-lock (WORM) GCS bucket** | Hard delete after 6 y by lifecycle rule | Resolves the 3-way conflict between `AuditLog.cs:7` ("90 days"), Terraform `audit_retention_days = 90`, and [infrastructure.md](../infrastructure.md) ("6-year") — **6 years wins; fix the other two** |
| Consent records | Postgres compliance | Relationship duration + ⚖ 6 years | Never anonymized (they *are* the proof); hard delete at period end | Defense/accountability (GDPR Art. 5(2), 7(1)) |
| Erasure ledger | Postgres compliance | ⚖ 6 years | Hard delete | Proof of erasure; stores hashes only (§6) |
| Generated reports | Cache (`report:*` keys) | 1 hour TTL — **already implemented**, `ReportGenerationService.cs:21` | TTL expiry | Densest PHI artifact outside Postgres |
| Planned AI pipeline (Cosmos) | Cosmos DB | Per [llm_design.md](../llm_design.md): realtime 90 d, prediction cards 90 d, trends 2 y ⚖, digests 1 y ⚖ — enforce via **container TTL**, not jobs | TTL; erasure = delete by `wearerUserId` partition | |
| Per-user LSTM models (planned) | Blob | Life of subject | Delete at erasure (model weights are personal data — trained on one person) | |
| APM/telemetry (Datadog/Better Stack) | SaaS | ⚖ 30 days — configure in-product retention; **stop shipping raw member GUIDs in paths** (scrub processor, §7) | Provider-side expiry | |
| DB backups | Cloud SQL | 7 days (`cloud_sql.tf:92`) — becomes the erasure bound (§6) | Automatic expiry | |

### 5.2 `DataRetentionWorker` (net-new, lives in `CardiTrack.Worker` per CLAUDE.md)

Design constraints from §1.12: must take a **Postgres advisory lock** (3 Cloud Run instances), must have an error boundary, must batch (a 25-month purge over years of accumulation cannot be one statement), must be observable and dry-runnable.

```csharp
// src/Worker/CardiTrack.Worker/Workers/DataRetentionWorker.cs
public sealed class DataRetentionWorker(
    IConfiguration cfg, IServiceScopeFactory scopes, ILogger<DataRetentionWorker> log)
    : CronBackgroundService(cfg["Workers:DataRetentionWorker:CronExpression"] ?? "0 0 2 * * *")
{
    protected override async Task ExecuteJobAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db  = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        var opt = scope.ServiceProvider.GetRequiredService<IOptions<RetentionOptions>>().Value;

        // One runner across all instances; skip (don't queue) if another holds it.
        if (!await db.TryAdvisoryLockAsync(RetentionLockId, ct)) return;
        try
        {
            var run = await RetentionRun.StartAsync(db, opt.DryRun, ct);   // compliance.retention_runs
            foreach (var policy in opt.Policies.Where(p => p.Enabled))
            {
                try   { run.Record(policy, await ApplyAsync(db, policy, opt, ct)); }
                catch (Exception ex) { run.RecordFailure(policy, ex); }     // one bad policy ≠ dead job
            }
            await run.CompleteAsync(ct);   // summary row: per-policy rows affected — the audit evidence
        }
        finally { await db.ReleaseAdvisoryLockAsync(RetentionLockId, ct); }
    }

    private static async Task<int> ApplyAsync(CardiTrackDbContext db, RetentionPolicy p, RetentionOptions o, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - p.MaxAge;
        var total = 0;
        while (!ct.IsCancellationRequested)
        {
            // Batched, keyset-paged. DryRun => count only.
            var affected = p.Action switch
            {
                RetentionAction.HardDelete       => await p.ExecuteDeleteBatchAsync(db, cutoff, o.BatchSize, o.DryRun, ct),
                RetentionAction.AnonymizeInPlace => await p.ExecuteAnonymizeBatchAsync(db, cutoff, o.BatchSize, o.DryRun, ct),
                RetentionAction.ArchiveThenDelete=> await p.ExecuteArchiveBatchAsync(db, cutoff, o.BatchSize, o.DryRun, ct),
            };
            total += affected;
            if (affected < o.BatchSize) break;
            await Task.Delay(o.InterBatchDelay, ct);   // don't starve the sync workload
        }
        return total;
    }
}
```

```jsonc
// appsettings: WorkerOptions today is a bare cron string (WorkerOptions.cs:3-6) — extend:
"Workers": {
  "DataRetentionWorker": {
    "CronExpression": "0 0 2 * * *",
    "DryRun": false,
    "BatchSize": 5000,
    "Policies": [
      { "Name": "activity-logs",     "Table": "activity_logs",     "MaxAgeDays": 760, "Action": "HardDelete",        "Enabled": true },
      { "Name": "pattern-baselines", "Table": "pattern_baselines", "MaxAgeDays": 365, "Action": "HardDelete",        "Enabled": true },
      { "Name": "alerts",            "Table": "alerts",            "MaxAgeDays": 730, "Action": "AnonymizeInPlace",  "Enabled": true },
      { "Name": "audit-archive",     "Table": "audit_logs",        "MaxAgeDays": 365, "Action": "ArchiveThenDelete", "Enabled": true },
      { "Name": "audit-final",       "Bucket": "audit-archive",    "MaxAgeDays": 2190, "Action": "HardDelete",       "Enabled": true }
    ]
  }
}
```

Anonymize-in-place method for alerts (the one category kept as skeletons):
`UPDATE alerts SET title = alert_type, message = '', metric_values = NULL, acknowledged_by_user_id = NULL, resolved_by_user_id = NULL WHERE …` — i.e., reduce the row to `(random Id, CardiMemberId, type, severity, dates)`. Note this is **pseudonymized retention, not anonymization in the GDPR sense** (still keyed to CardiMemberId until the member is erased or the row ages out) — named accurately so nobody mistakes it for Tier 3.

---

## 6. GDPR erasure pipeline

### 6.1 Model

Erasure is a **stateful, resumable workflow** (not a single transaction) because it spans Postgres, the pii vault, Google token revocation, Auth0, cache, telemetry, planned Cosmos/Blob, and backups.

```sql
CREATE TABLE compliance.erasure_requests (
    id                  uuid PRIMARY KEY,
    subject_type        text NOT NULL,      -- 'cardi_member' | 'user'
    subject_id          uuid NOT NULL,
    requested_by_user_id uuid NOT NULL,
    authority_basis     text NOT NULL,      -- 'self' | 'authorized_representative' | 'account_admin' (validity: D4)
    status              text NOT NULL,      -- received → verified → processing → completed → (rejected)
    received_at         timestamptz NOT NULL,
    due_at              timestamptz NOT NULL,   -- received + 30 days (Art. 12(3))
    completed_at        timestamptz,
    steps               jsonb NOT NULL DEFAULT '[]'   -- [{step, status, at, rowsAffected}]
);

-- Survives the erasure itself; contains NO identifiers:
CREATE TABLE compliance.erasure_ledger (
    id            uuid PRIMARY KEY,
    subject_hash  bytea NOT NULL,      -- HMAC(subject_id, ledger_salt) — for backup-restore replay
    subject_type  text NOT NULL,
    erased_at     timestamptz NOT NULL,
    tables_swept  jsonb NOT NULL
);
```

### 6.2 `ErasureWorker` step sequence (CardiMember erasure)

```
 1. VERIFY      Authority check per D4 (who may request erasure for the wearer).
 2. HALT INTAKE Set DeviceConnections → Disconnected; WearableSyncWorker's
                GetDueForSyncAsync already filters on status — no new inflow.
 3. REVOKE      POST each refresh token to Google's revoke endpoint, then null
                token columns. (Provider-side link dies even if we crash here.)
 4. SWEEP       Iterate SubjectDataMap.ByCardiMember (§3.4), batched hard
                deletes: activity_logs, alerts (+notes/photos+blobs),
                pattern_baselines, device_connections. Planned: Cosmos delete
                by wearerUserId partition; delete per-user LSTM blob.
 5. RELATIONSHIPS  Delete user_cardi_members rows (FK cascade exists), then the
                clinical cardi_members row.
 6. CRYPTO-SHRED  pii.subject_identities: null dek_wrapped, null
                payload_ciphertext, set shredded_at. Identity is now
                unrecoverable INCLUDING in every backup taken while the DEK
                design was in force.
 7. CACHE/DERIVED  Purge report:* cache keys for the member's reports.
 8. SUBPROCESSORS  Telemetry: member GUID now maps to nothing (see step 10);
                if legal classifies APM data as personal data, file provider
                deletion API calls here (D6).
 9. LEDGER     Write erasure_ledger row (hash only). Audit rows REMAIN —
                Art. 17(3)(b) legal-obligation exemption (HIPAA 6-year audit
                duty) — flagged as D5 for legal sign-off.
10. BACKUP BOUND  Do nothing active: Cloud SQL PITR/backups expire in 7 days
                (cloud_sql.tf:87-93). Erasure SLA to the data subject =
                30 days (Art. 12(3)) ≫ 7-day backup horizon. RESTORE RULE
                (runbook + automated post-restore hook): after any restore,
                re-run the sweep for every erasure_ledger row with
                erased_at > restore point, matching on subject_hash.
11. CONFIRM    Notify requester; mark completed.
```

User (caregiver) erasure follows the same pattern via `SubjectDataMap.ByUser`: delete the `Users` row, **Auth0 Management API `DELETE /api/v2/users/{auth0UserId}`** (extends the existing `Auth0ManagementClient`, `ExternalClients/Auth0ManagementClient.cs`), null `acknowledged_by`/`resolved_by` references, delete push tokens/preferences. If the user is an org's last admin, the request escalates to organization closure (product flow needed — D4).

**API surface (net-new):** `DELETE /api/v1/cardimembers/{id}` and `DELETE /api/v1/users/me` create `erasure_requests` rows (they do not delete inline); `GET /api/v1/erasure-requests/{id}` reports status. A DSAR **export** endpoint (Art. 15/20 — JSON bundle of vault identity + clinical rows + consent history) reuses `SubjectDataMap` for coverage and should ship in the same phase.

---

## 7. Access & security controls

### 7.1 Encryption

| Layer | Today | Target |
|-------|-------|--------|
| In transit | TLS 1.2+ at GCLB (`load_balancer.tf:39-44`), Cloud SQL `ENCRYPTED_ONLY`, internal-only MedGemma | Keep; add `UseHsts()` to the API (currently Web only) |
| At rest (platform) | Cloud SQL/GCS default encryption | Keep |
| At rest (field) | AES-256-GCM, single static key, tokens only; format `nonce‖tag‖ct` (`AesEncryptionService.cs:63-67`) | **v2 envelope format: `keyId‖nonce‖tag‖ct`.** Decrypt routes on `keyId` (legacy blobs = implicit `v1`); rotation = new key version + lazy re-encrypt on write. Vault payloads use per-subject DEKs wrapped by **Cloud KMS** (net-new Terraform: key ring + KEK + IAM binding to the identity service account only) |
| Immediate fix | — | **Encrypt `MedicalNotes` now** (close §1.2) via an EF value converter bound to `IEncryptionService`, ahead of the vault migration |

### 7.2 Audit logging — give `AuditLogs` a writer

Two complementary mechanisms, both writing the existing `AuditLogs` entity (append-only role):

```csharp
// (a) WRITES — EF SaveChanges interceptor (the hook point already noted at
//     CardiTrackDbContext.SaveChanges): for every Added/Modified/Deleted entity
//     in the PHI set, emit {UserId (ambient from UserContextMiddleware),
//     CardiMemberId, Action, EntityType, EntityId, ChangedFields (names only
//     — NEVER before/after values for clinical/PII columns)}.
public sealed class PhiAuditSaveChangesInterceptor : SaveChangesInterceptor { /* … */ }

// (b) READS — endpoint filter on PHI routes (dashboard, health-data, reports,
//     insights, identity-vault calls): {UserId, CardiMemberId, Action="Read",
//     RequestPath, ResponseStatus, IpAddress, UserAgent}. Route → CardiMemberId
//     comes from the route values already present on those endpoints.
public sealed class PhiReadAuditFilter : IEndpointFilter { /* … */ }
```

Every `IIdentityVault` call is additionally audited server-side (category (b) with `EntityType = SubjectIdentity`) — re-identification events are exactly what an investigator asks for.

`ChangedFields` stores **field names, not values** — otherwise the audit trail itself becomes a PHI store with a 6-year life. (The current entity comment already says "summary… not the actual data"; the interceptor enforces it.)

### 7.3 RBAC & separation of duties

- **Application RBAC (exists, keep):** Auth0 JWT → `UserRole` + `UserCardiMember.CanViewHealthData` scoping. Centralize the check in one authorization handler so every PHI endpoint declares `[Authorize(Policy = "ViewMemberHealthData")]` instead of ad-hoc repository filters.
- **Fix §1.8:** `GetStatusAsync`/`DownloadAsync` must verify `requestingUserId` owns the report.
- **DB roles (net-new):** `app_rw` (clinical, no `pii.*`), `identity_svc` (pii only), `app_append` (compliance INSERT), `retention_svc` (DELETE/UPDATE where the policies need it). Separate Cloud Run service accounts for API vs Worker (today both effectively share default compute SA — also carries `storage.objectAdmin`, over-broad).
- **The application layer cannot reach the re-identification keys:** KMS `decrypt` on the vault KEK and the `export_salt` is IAM-granted only to the identity service path / export job SA. A compromised API pod can read clinical GUIDs but can neither query `pii.*` (no grant) nor unwrap DEKs (no KMS binding).

### 7.4 Third-party egress controls

- **Gemini (§1.7): stop sending the name immediately** — replace `## Patient: {member.Name}` with a neutral label; re-insert the display name into the rendered report *after* the LLM call, inside our estate. Follow-up decision (D6): move report/chat generation to Vertex AI under the Cloud BAA/DPA, or route to the in-VPC MedGemma. Also move the API key from query string to header.
- **Telemetry:** Serilog enricher + OTel processor that replaces `/cardimembers/{guid}` path segments with `/cardimembers/{redacted}` before shipping; exception messages scrubbed against the same rule. Configure provider-side retention (30 d ⚖).
- **Mobile RUM:** replace hardcoded `TrackingConsent.Granted` (`MobileApm.cs:71`) with `Pending` until the user consents in-app; drop `SessionSampleRate` from 100 to an operationally sufficient rate.
- **Push (planned):** design rule — FCM/APNs payloads carry only `{alertId, severity}`; the app fetches content over authenticated API. Push infrastructure then never needs a BAA for content.

---

## 8. Consent & lawful basis model

Builds the documented-but-unbuilt `ConsentRecords` ([infrastructure.md](../infrastructure.md) DDL) with the fields GDPR accountability actually needs:

```sql
CREATE TABLE compliance.consent_records (          -- APPEND-ONLY (no UPDATE/DELETE grants)
    id                   uuid PRIMARY KEY,
    cardi_member_id      uuid NOT NULL,
    policy_version       text NOT NULL,            -- e.g. 'privacy-2026-08' — exact accepted text version
    policy_sha256        bytea NOT NULL,           -- hash of the rendered consent text shown
    lawful_basis         text NOT NULL,            -- 'explicit_consent_art9_2a' (default; D3)
    share_activity       boolean NOT NULL,
    share_heart_rate     boolean NOT NULL,
    share_sleep          boolean NOT NULL,
    consented_by_user_id uuid,                     -- NULL when wearer self-consents via own login
    consented_by_name    text NOT NULL,            -- Tier 1: excluded from every export
    on_behalf_basis      text,                     -- 'self' | 'legal_representative' | 'family_attestation' (D4)
    consent_method       text NOT NULL,            -- 'digital_signature' | 'verbal_confirmed'
    action               text NOT NULL,            -- 'granted' | 'modified' | 'withdrawn'
    created_at           timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_consent_member_time ON compliance.consent_records (cardi_member_id, created_at DESC);
```

- **Current state = latest row** per member (matches the documented append-only design). Withdrawal is a new row with `action='withdrawn'`, never an update.
- **Compliance queries this must answer** (and now can, each in one indexed query): *what had this member consented to on date X; which policy text; who recorded it; when was it withdrawn; list all members processing under policy version V* (for re-consent campaigns when the policy text changes).
- **Enforcement points** — consent is checked where data *moves*, not just at UI:
  1. `DeviceSyncService.SyncCardiMemberAsync` skips metric groups without a current grant (activity/HR/sleep map 1:1 to the three Google Health scope families in `appsettings.json:77-81`).
  2. The planned AI pipeline's aggregator applies the same gate per [llm_design.md](../llm_design.md) ("data types without recorded consent are never processed").
  3. Family visibility continues through `CanViewHealthData` — that flag is *authorization* (what a caregiver may see), consent records are *lawful basis* (what CardiTrack may process). Keep them distinct.
- **Withdrawal side-effects:** withdrawal of a metric stops sync + processing of that metric immediately; withdrawal of everything triggers the disconnect flow (token revoke) and offers erasure (§6) — withdrawal of consent and erasure are separate GDPR rights and remain separate actions.

---

## 9. Subprocessor register

Everywhere data leaves the primary Postgres/VPC boundary, with the paperwork each requires **before** PHI/personal data may flow. "Blocked" = data flows today without the paperwork.

| Party | Data leaving | HIPAA | GDPR | Status / action |
|-------|-------------|-------|------|-----------------|
| **Google Cloud** (Cloud SQL, GCS, Secret Manager, KMS, Cloud Run) | All stored data | BAA available — **execute it** and confirm each service is on Google's HIPAA-covered list | Cloud Data Processing Addendum (auto-incorporated) + SCCs; region already EU (`infrastructure/main.tf`) | ⚠ Execute BAA (D1 determines whether required) |
| **Google Gemini API** (`generativelanguage.googleapis.com`) | Patient name + daily readings (reports); 3-day metrics + free text (chat) | **Not covered by the Cloud BAA** — consumer/developer API | Separate terms; prompt-logging posture must be verified | 🔴 **Blocked as used.** Immediate: strip identifiers (§7.4). Decision D6: Vertex AI (BAA-eligible) or in-VPC MedGemma |
| **Google Health API** | Inbound wearable data; outbound: OAuth tokens, revocations | Google here is the wearer-authorized **source**, not our subprocessor; restricted-scope verification + CASA gates production ([oauth_clients.md](./oauth_clients.md)) | Independent-controller relationship; disclose in privacy notice | On track (verification pending) |
| **Auth0 (Okta)** | Caregiver emails, names, Auth0UserIds | PII not PHI, but sits in the auth path of a health app — BAA available on suitable plan | DPA + SCCs (Okta standard) | ⚠ Execute DPA; confirm plan tier |
| **Datadog / Better Stack** (APM, logs; mobile RUM) | Request paths w/ member GUIDs, exceptions, DB spans, session replays | Pseudonymous identifiers linked to a health service ⇒ treat as PHI-adjacent; Datadog offers BAAs; **Better Stack: verify or drop for prod** | DPA + SCCs; retention config; RUM requires consent (currently hardcoded — §7.4) | 🔴 Scrub + consent-gate first; paperwork per D6 |
| **HuggingFace** | Nothing (model weights inbound only) | — | — | OK |
| **Microsoft Azure** (planned AI pipeline: Functions, Event Hubs, Cosmos, Blob, Notification Hubs, ACA) | Readings, inference results, digests | Microsoft BAA before first PHI event; **verify Notification Hubs is on the covered-services list** (routes to FCM/APNs — keep payloads content-free per §7.4) | Microsoft Products & Services DPA + SCCs; pin EU regions (doc example uses `swedencentral` ✓) | Pre-launch gate for the pipeline |
| **Stripe** (planned) | Payment data only — subscription metadata must never reference health status | No BAA needed if boundary holds (document it) | DPA (standard) | Design rule |
| **Twilio / Azure Communication Services** (planned SMS fallback) | Alert content to phone numbers | SMS body = PHI ⇒ BAA required; or keep bodies content-free ("Check the CardiTrack app") | DPA + SCCs | Decide content-free vs BAA before shipping |
| **FCM / APNs** (planned push) | Token + content-free payload (§7.4 rule) | No BAA needed if payloads stay content-free | Disclose in notice | Design rule |

**Process rule:** adding any new external destination for Tier 1/Tier 2 data requires a row in this table *and* a signed BAA/DPA reference **before** the integration merges. Enforce with a PR checklist item; the `SubjectDataMap` architecture test (§3.4) is the analogous in-schema guard.

---

## 10. Decisions required from legal/compliance (not engineering)

Engineering builds the mechanisms above regardless; these determine configuration and paperwork. **D1 gates several others.**

| # | Decision | Why it's legal, not engineering |
|---|----------|--------------------------------|
| **D1** | **Is CardiTrack a HIPAA covered entity, a business associate, or neither?** Direct-to-consumer wellness monitoring is typically *outside* HIPAA (no provider/plan/clearinghouse relationship) — but the **Business org type (care homes)** likely makes CardiTrack a **business associate** of covered-entity customers, pulling the full Security/Privacy Rule in via BAAs we'd have to sign *with them*. If HIPAA doesn't attach, the **FTC Health Breach Notification Rule** does. This ADR assumes HIPAA-grade controls either way (they're also the right GDPR Art. 32 posture) | Entity-status determination |
| **D2** | Ratify every ⚖ retention period in §5.1 — what is "necessary" (GDPR Art. 5(1)(e)) per category | Proportionality judgment |
| **D3** | Lawful basis per processing purpose: core monitoring presumably **explicit consent** (Art. 9(2)(a)); confirm basis for family sharing, AI inference, and product analytics separately (consent vs. legitimate interest + Art. 9 condition) | Basis selection |
| **D4** | **Who may consent, and who may request erasure, on behalf of the wearer?** Today an account Admin records consent with `verbal_confirmed` as an option, and wearers often have no login. Validity of proxy consent for a capable adult — and the capacity/representation rules — is a pure legal question, and it shapes onboarding UX | Capacity & representation law |
| **D5** | Confirm the **erasure exemptions**: audit logs (6 y) and consent records retained post-erasure under Art. 17(3)(b) | Exemption applicability |
| **D6** | Third-party AI + telemetry: approve Vertex-AI-with-BAA vs. in-VPC-only for LLM features; approve (or replace) Better Stack; classify APM data for DSAR/erasure purposes | Vendor risk & contracts |
| **D7** | Sign off that Tier 3 output (Safe Harbor + k-anonymity gate) is treated as **anonymous under GDPR** given the residual-means test (Recital 26) — or keep treating it as personal data | Anonymity threshold judgment |
| **D8** | If daily-granularity research/analytics data is wanted: commission **Expert Determination** (§164.514(b)(1)) — Safe Harbor cannot produce it | Requires certified expert engagement |
| **D9** | Ratify k = 5, the quasi-identifier set, and per-batch salt rotation (vs. stable longitudinal IDs) in §4.3 | Risk-appetite threshold |
| **D10** | International transfers: confirm SCC/DPF coverage for each US-headquartered processor (Auth0/Okta, Datadog, Stripe, Twilio) given EU-resident data | Transfer-mechanism selection |

---

## 11. Implementation phases

| Phase | Contents | Depends on |
|-------|----------|------------|
| **P0 — defect fixes** (no schema change) | Encrypt `MedicalNotes` (§7.1); strip name from Gemini prompts + key-to-header (§7.4); report ownership check (§1.8); telemetry GUID scrubbing + RUM consent gate (§7.4); drop dead `PasswordHash` column (`Encryption:IV` config — done); API `UseHsts()` | — |
| **P1 — audit + consent** | `PhiAuditSaveChangesInterceptor` + `PhiReadAuditFilter` writing `AuditLogs`; `compliance.consent_records` + endpoint + sync-gate enforcement; resolve the 90-day/6-year audit retention conflict (Terraform + entity comment → 6 y) | — |
| **P2 — retention** | `RetentionOptions` + `DataRetentionWorker` with advisory lock; advisory-lock + error-boundary hardening of `CronBackgroundService`; audit archive to bucket-lock GCS; retention_runs evidence table | P1 (audit) |
| **P3 — erasure + DSAR** | `SubjectDataMap` + architecture test; `erasure_requests`/`erasure_ledger`; `ErasureWorker`; DELETE + export endpoints; Auth0 delete + Google token revoke; post-restore replay hook | P1, D4 |
| **P4 — identity vault** | `pii` schema, per-subject DEKs via Cloud KMS (Terraform), `IIdentityVault` + DB roles/SA split; `CardiMembers` slimming migration (`BirthYear`/`IsOver89`); envelope-versioned `AesEncryptionService` v2 | P1; crypto-shred step of P3 upgrades automatically |
| **P5 — Tier 3 analytics** | `SafeHarborDeidentifier` + fail-closed policy tests; `KAnonymityGate`; export manifests; KMS-held `export_salt` | P4, D7/D9 |
| Gate | AI pipeline ([llm_design.md](../llm_design.md)) ships only after: Microsoft BAA (if D1 requires), Cosmos TTLs, consent gate in aggregator, erasure-by-partition wired into `SubjectDataMap` | P3 |

---

*Prepared as an architecture decision record. File issues per phase; each phase lands as an independent PR series.*
