# Data Protection Architecture — HIPAA / GDPR Retention & De-Identification

**Status:** Proposed (ADR — awaiting engineering review and the legal/compliance decisions in [§10](#10-decisions-required-from-legalcompliance-not-engineering))
**Scope:** Classification, pseudonymization, Safe Harbor de-identification, retention/deletion, access controls, consent, and subprocessor obligations for all medical/health data in CardiTrack.
**Relationship to other docs:** [infrastructure.md](../infrastructure.md) describes the deployed PostgreSQL (Cloud SQL) on GCP data model per `infrastructure/` Terraform. This ADR designs against the deployed system and notes where the planned AI pipeline ([llm_design.md](../llm_design.md)) must follow the same rules.
> **Platform note (Aug 7, 2026):** the AI pipeline design has been re-platformed from Azure to GCP (Pub/Sub + Cloud Run, with pipeline outputs as PostgreSQL JSONB tables — see [llm_design.md](../llm_design.md)). References to Azure services and Cosmos DB collections below describe the superseded design; the retention/TTL, consent, and erasure controls carry over unchanged to their GCP equivalents.
> **Platform note (Aug 12, 2026):** environmental-context enrichment ([llm_design.md](../llm_design.md)) is CardiTrack's first geolocation-derived data of any kind. It shipped ahead of the general `ConsentRecords` framework §8 designs, gated instead by one narrow, feature-specific, default-`false` flag (`CardiMember.EnvironmentalContextConsentGranted`) enforced at the single call site that can reach location data (`EnvironmentalEnrichmentService`) — a deliberately smaller mechanism than §8's per-metric consent model, not a substitute for it. Raw coordinates are never persisted; only derived temperature/air-quality values are. See the retention matrix (§5.1) and `SubjectDataMap` (§3.4) entries below.

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
| 2 | ~~**[BUG]** `MedicalNotes` is stored **in plaintext** despite comments claiming encryption~~ — **fixed.** `CardiMemberService` now encrypts on write and decrypts on read via `IEncryptionService` (AES-256-GCM), and, until the prompt-context framework arrived, it was the only server-side code that touched the column. **A second reader now exists** — `DemographicsContextSource` decrypts the note for the medical prompts (this section's own predicted case), using a shared `EncryptedFieldReader.Reveal` with the same plaintext-legacy fallback. It is a fix, not a regression: every prompt previously passed the stored column straight through, so the model was reading the ciphertext envelope rather than the note. A third reader is the signal to move to the value converter below. Applied at the service layer rather than the EF value converter suggested in §7.1, so that rows written before this change can be read back as plaintext instead of failing; every subsequent write re-stores them encrypted. **A value converter is still the more durable fix** if a second reader is ever added. The column was widened to `text` — base64 ciphertext over 2000 multi-byte characters exceeds the old `varchar(2000)`. | `src/Core/CardiTrack.Application/Services/CardiMemberService.cs` (`Protect`/`Reveal`), `src/Infrastructure/CardiTrack.Infrastructure/Persistence/Configurations/CardiMemberConfiguration.cs` |
| 3 | ~~**[GAP]** `AuditLogs` table exists with HIPAA-labelled indexes but **nothing ever writes to it** — zero PHI access is audited~~ — **fixed for read access.** `AuditLoggingMiddleware` writes read-access rows via `IAuditLogRepository` on the eight controllers annotated with `AuditHealthDataAccessAttribute`. The write-side `SaveChanges` interceptor of §7.2(a) is still net-new | Entity `src/Core/CardiTrack.Domain/Entities/AuditLog.cs`, config `src/Infrastructure/CardiTrack.Infrastructure/Persistence/Configurations/AuditLogConfiguration.cs:66-72`; middleware `src/Presentation/CardiTrack.API/Middleware/AuditLoggingMiddleware.cs` |
| 4 | **[GAP]** The `ConsentRecord` entity is specified in docs but **not built**; the only consent-shaped data is `UserCardiMember.CanViewHealthData`/`ReceiveAlerts` (mutable booleans, no history) and a banner-dismissal timestamp | Spec: [entity_summary.md](./entity_summary.md) §13, [infrastructure.md](../infrastructure.md) `ConsentRecords` DDL; flags at `src/Core/CardiTrack.Domain/Entities/UserCardiMember.cs:13-14`; `User.HealthDataDisclosureDismissedDate` at `src/Core/CardiTrack.Domain/Entities/User.cs:22` |
| 5 | **[GAP]** **No general retention job and no erasure path.** DELETE endpoints now exist for CardiMembers and device connections, but both are **soft deletes** — they flip `IsActive` and discard OAuth tokens; no PHI row is ever removed, and `ActivityLogs`/`Alerts`/`PatternBaselines` for a removed member are left live and queryable (see finding 6). Worker now hosts **thirteen** workers, and partition-based retention for the sub-daily/pipeline tables *is* enforced by `PartitionMaintenanceWorker` (granular 90 d, hourly rollups 13 mo, **digests and journal entries 7 mo**, assessments 90 d, environmental 90 d) — but there is still no `DataRetentionWorker` for `ActivityLogs`/`Alerts`/`PatternBaselines`, and still no erasure path | `src/Worker/CardiTrack.Worker/Workers/` (thirteen workers, incl. `PartitionMaintenanceWorker.cs`); `CardiMemberService.RemoveAsync`, `DeviceConnectionService.DisconnectAsync` |
| 6 | **[GAP]** Almost **no foreign keys** (deliberate — [entity_summary.md](./entity_summary.md) "Design Principles"). Only `UserCardiMembers` and `Subscriptions` cascade. Deleting a CardiMember silently orphans all `ActivityLogs`, `Alerts`, `PatternBaselines`, `DeviceConnections`, `AuditLogs` rows — they stay live and queryable (those tables don't implement `ISoftDeletable`, and there is no global query filter) | `src/Infrastructure/CardiTrack.Infrastructure/Migrations/20260312180945_InitialCreate.cs` (one `table.ForeignKey`); `src/Infrastructure/CardiTrack.Infrastructure/Persistence/CardiTrackDbContext.cs:29-35` (no `HasQueryFilter`) |
| 7 | ~~**[BUG]** **PHI leaves the estate identifiable:** report generation concatenates the wearer's real name plus day-by-day readings into a Gemini prompt (`generativelanguage.googleapis.com` — *not* covered by a Google Cloud BAA)~~ — **fixed.** Prompts now label members positionally (Patient A/B), the display name is re-inserted after the LLM response inside our estate, and the API key moved from the query string to the `x-goog-api-key` header (§7.4). The BAA/provider decision (D6) is still open | `src/Infrastructure/CardiTrack.Infrastructure/Services/ReportGenerationService.cs`, `ExternalClients/General/GeminiClient.cs` |
| 8 | ~~**[BUG]** Report cache has no ownership check — any authenticated caller with a report ID can download another family's report~~ — **fixed.** `GetStatusAsync`/`DownloadAsync` verify `requestingUserId` owns the report (§7.3) | `ReportGenerationService.cs` |
| 9 | Encryption service is sound (AES-256-GCM) but has a **single static key with no key ID in the ciphertext** — no rotation path, and crypto-shredding-based erasure is not currently expressible. It now has **four** consumers — device OAuth tokens, `MedicalNotes`, questionnaire Q&A text, and `PushDeviceToken.Token` (looked up via a SHA-256 `TokenFingerprint`) — so the single-static-key limitation spans four field families, raising the cost of the v2 envelope migration the longer it waits | `src/Infrastructure/CardiTrack.Infrastructure/Security/AesEncryptionService.cs:63-67`; consumers: `Services/DeviceConnectionService.cs`, `ExternalClients/OAuthTokenRefreshService.cs`, `CardiMemberService`, `QuestionnaireService`, push-token persistence |
| 10 | Telemetry ships request paths containing `{cardiMemberId}` GUIDs, exception payloads, and Npgsql spans to Datadog/Better Stack with no scrubbing or retention config. *(The mobile-RUM half of this finding — `TrackingConsent.Granted` at 100% session sampling — is **resolved by removal**: RUM was deleted in PR #185 because this org's Datadog site is unreachable from the mobile SDK. No RUM sessions are collected or sampled. `TrackingConsent.Granted` still applies to mobile logs/traces, so the consent question survives at a much smaller surface — see §7.4.)* | `src/Infrastructure/CardiTrack.Observability/ApmExtensions.cs:87-151`, `src/Presentation/CardiTrack.Mobile/Services/MobileApm.cs` |
| 11 | Backups/versioning defeat naive deletion claims: Cloud SQL `retained_backups = 7`, GCS bucket versioning on | `infrastructure/deployments/cloud_sql.tf:87-93`, `deployments/cloud_storage.tf:44-46` |
| 12 | `CronBackgroundService` has no distributed lock — unsafe for destructive jobs at `cloud_run_max_instances = 3`. *(The other half of the original finding — no error boundary — was fixed after the 2026-08-12 incident: each tick now runs inside a per-tick catch.)* | `src/Worker/CardiTrack.Worker/CronBackgroundService.cs:41-48,70-78`, `infrastructure/main.tf:46` |
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
| Medical notes (free text) | `CardiMembers.MedicalNotes` (AES-256-GCM, static key) | `pii.subject_identities`, encrypted with the subject DEK |
| Family questionnaire questions and answers | `MemberQuestionnaires.QuestionText` / `.AnswerText` (AES-256-GCM, static key, `QuestionnaireService`) | `pii.subject_identities`, encrypted with the subject DEK — caregiver-reported free text about the wearer, the same class as medical notes. Hard-deleted on request rather than soft-deleted (GDPR Art. 17); `TriggerContext` stays in the clear, being derived prose about readings already held, like `Alert.Message` |
| Caregiver name, email, phone, Auth0UserId | `Users` | stays in `Users` (account data, not PHI — but in GDPR erasure scope) |
| Device OAuth tokens | `DeviceConnections` (encrypted) | stays; re-keyed to versioned per-subject DEK (§7) |
| Device labels ("Mom's Fitbit"), consent signatory names | `DeviceConnections.DeviceName`, future `ConsentRecords` | treat as identifier-bearing free text: never exported, never sent to LLMs |
| IP address, user agent | `AuditLogs` | stays (audit plane; exempt from erasure — §6) |
| Push tokens (**built 2026-08-11**) | `PushDeviceToken.Token` per [notification_engine.md](./notification_engine.md) §8 | **Built to this spec: encrypted with `IEncryptionService` (the `DeviceConnections` pattern), indexed on a SHA-256 fingerprint rather than the ciphertext. The 30-day hard delete after disable is still unenforced — no retention job yet (finding #5).** A push token is a stable cross-reinstall device identifier (Safe Harbor category 13, §4.2) and, paired with a leaked FCM credential, lets an attacker push to a named caregiver's phone |
| Stripe customer/subscription IDs (planned) | — | Tier 1 on arrival |

### Tier 2 — Pseudonymized clinical plane

Health payload keyed only by `CardiMemberId` (a random GUID that carries no identity by itself). This is where `ActivityLogs`, `Alerts`, `PatternBaselines`, `DeviceConnections` (minus label), `EnvironmentalReadings`, and the planned Cosmos collections live.

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
        new(nameof(EnvironmentalReading), "environmental_readings", "cardi_member_id"), // built 2026-08-12; no coordinate columns to erase, only derived values
        // CardiMembers.PhotoObjectName (built 2026-08-18) names a blob OUTSIDE Postgres: the
        // sweep must also delete gs://<member-photos-bucket>/members/{cardiMemberId}/ recursively.
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
| 3 | All date elements < year (incl. DOB, admission/service dates); ages > 89 | `DateOfBirth`; `ActivityLogs.Date`, `SleepStartTime/EndTime`, `LongestSedentaryStretchStartUtc`; `Alert.TriggeredDate`; `PatternBaseline.CalculatedDate`, `TypicalBedtime/WakeTime` | **Generalize**: DOB → year; age > 89 → `90+`; reading dates → year (or month-of-year *count* aggregates); absolute sleep timestamps → durations only. Clock-time-of-day fields (`TypicalBedtime`) are quasi-identifiers → §4.3 |
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
| 17 | Full-face photos & comparable images | `CardiMember.PhotoObjectName` → blob in the private member-photos GCS bucket (**built 2026-08-18**; served only via ≤15-min V4 signed URLs, EXIF/GPS stripped at upload, blob hard-deleted on replace/remove/member removal); `AlertPhotos` (planned) | Strip |
| 18 | Any other unique identifying number/characteristic/code | `CardiMemberId`, `Auth0UserId`, `OrganizationId` | **Replace** with `AnalyticsId` (below); strip the rest. Free text categorically excluded |

**Re-identification code (§164.514(c)):** the export key is
`AnalyticsId = HMAC-SHA256(CardiMemberId, export_salt)` with `export_salt` held only in Cloud KMS/Secret Manager, IAM-granted to the export job SA — **not** to the API or Worker general roles. This satisfies §164.514(c): not derived from patient identifiers (input is a random GUID), and the means of re-identification (salt) is not disclosed alongside the data.

**Implementation — pure function + fail-closed policy:**

```csharp
public sealed record DeidentifiedDailyRecord(
    string AnalyticsId, int Year, string AgeBand, string Gender, string DeviceType,
    int? Steps, int? RestingHeartRate, int? AvgHeartRate,
    int? SleepMinutes, int? SleepEfficiency, decimal? SpO2Average /* …metrics only */);
// No HrvAverage: GoogleHealthApiClient never fetches HRV, so no such source
// property exists to de-identify (phantom field removed 2026-08-18).

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
| Minute-grain readings (`GranularMetricHours`) | Postgres clinical | **90 days** — enforced today by `PartitionMaintenanceWorker` | **Partition drop** (daily partitions; instant, no dead tuples) | Substrate for moving-window inference only ([granular ADR](./granular_timeseries_storage.md)); aligned with the AI pipeline's `realtime_results` window |
| Hourly rollups (`MetricRollupsHourly`) | Postgres clinical | **13 months** — enforced today by `PartitionMaintenanceWorker` | **Partition drop** (monthly partitions) | A year of hour-grain comparisons plus slack ([granular ADR](./granular_timeseries_storage.md)) |
| Environmental readings (`EnvironmentalReadings`) | Postgres clinical | **90 days** — enforced today by `PartitionMaintenanceWorker` | **Partition drop** (daily partitions) | Derived temperature/AQI only, no coordinates stored; matches the `RealtimeAssessments` window it feeds prompts alongside. Populated only for members with `EnvironmentalContextConsentGranted = true` |
| Derived baselines (`PatternBaselines`) | Postgres clinical | ⚖ 12 months (keep latest per period regardless) | Hard delete — fully regenerable | Derived data |
| Profile photos (`CardiMember.PhotoObjectName` + GCS blob) | Postgres clinical (object name) / private GCS bucket (blob) | **Life of the active membership** — built 2026-08-18 | **Hard delete of the blob** on photo replace, photo removal, and member removal (soft delete included — the photo does not share health history's retention); at erasure, `gs://<bucket>/members/{id}/` is swept recursively. Backstop enforcement by `OrphanedPhotoCleanupWorker` (daily): deletes blobs no active member references once >24 h old, and clears photos a crashed removal left on soft-deleted rows | Full-face photo = Safe Harbor category 17, Tier 1; served only via ≤15-min signed URLs, never a durable link |
| Alerts + notes/photos | Postgres clinical / GCS | ⚖ 24 months after resolution | **Anonymize-in-place**: null `AcknowledgedBy`/`ResolvedBy`, replace `Title`/`Message`/`MetricValues` with type+severity codes; delete photos (blobs + rows). Row skeleton retained for alert-quality stats | Free text + user refs are the risk; counts are the value |
| Device connections + OAuth tokens | Postgres clinical | Life of connection; **tokens purged ≤ 24 h after disconnect/consent-withdrawal**, with provider-side revoke (`https://oauth2.googleapis.com/revoke`) | Hard delete of token columns; connection row hard-deleted at member erasure | Live credentials to third-party PHI |
| Wearable battery reading (`BatteryLevel`/`BatteryStatus`/`BatteryUpdatedAt`) | Postgres clinical, on the connection row | **Last value only** — each sync overwrites; no history table and no time series | Removed with the connection row at member erasure | Hardware telemetry, not PHI — charge level of the device, nothing about the wearer |
| Audit logs (`compliance.audit_logs`) | Postgres → GCS archive | **6 years** (HIPAA §164.316(b)(2)(i)); 1 year hot, then export to a **bucket-lock (WORM) GCS bucket** | Hard delete after 6 y by lifecycle rule | Resolves the 3-way conflict between `AuditLog.cs:7` ("90 days"), Terraform `audit_retention_days = 90`, and [infrastructure.md](../infrastructure.md) ("6-year") — **6 years wins; fix the other two** |
| Consent records | Postgres compliance | Relationship duration + ⚖ 6 years | Never anonymized (they *are* the proof); hard delete at period end | Defense/accountability (GDPR Art. 5(2), 7(1)) |
| Erasure ledger | Postgres compliance | ⚖ 6 years | Hard delete | Proof of erasure; stores hashes only (§6) |
| Generated reports | Cache (`report:*` keys) | 1 hour TTL — **already implemented**, `ReportGenerationService.cs:22` | TTL expiry | Densest PHI artifact outside Postgres |
| Push tokens (`PushDeviceToken`) | Postgres clinical | **30 days after disable** | **Hard delete** — never soft-retained | Tier 1 device identifier; a disabled token has no operational value and every retained day is exposure ([notification_engine.md](./notification_engine.md) §7.2 C2) |
| Notifications + delivery outbox | Postgres clinical | ⚖ 180 d resolved / 90 d delivered | Hard delete. Rows hold no names — `TemplateData` is counters only, names resolve from the vault at render time | Keeps the completeness-nudge surface out of the identifier↔clinical join that finding #1 describes |
| AI pipeline results (`RealtimeAssessments`, `DigestEntries`, `EnvironmentalReadings`) | Postgres clinical (partitioned) | Per [llm_design.md](../llm_design.md): assessments 90 d, **digests and CardiJournal entries 7 mo**, environmental 90 d — **enforced today** by `PartitionMaintenanceWorker` partition drops. The journal window is the longest history any plan sells plus margin, applied uniformly — the minimisation trade-off that makes is DPIA open item **OI-14** | Partition drop; erasure = delete by `CardiMemberId` | Per-user LSTM model blobs were descoped with the LSTM (2026-08-10) — no model-weight artifacts exist |
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
                pattern_baselines, device_connections, notifications +
                notification_deliveries scoped to the member, plus the member's
                rows/partitions in the pipeline result tables (CardiMemberId).
                Blob stores too: the profile photo under
                gs://<member-photos-bucket>/members/{cardiMemberId}/ (the
                CardiMembers.PhotoObjectName pointer dies with the row in
                step 5; normal member removal already hard-deletes this blob).
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
| In transit | TLS 1.2+ at GCLB (`load_balancer.tf:39-44`), Cloud SQL `ENCRYPTED_ONLY`, MedGemma over HTTPS with an IAM-authorised OIDC token per call | Keep; add `UseHsts()` to the API (currently Web only) |
| At rest (platform) | Cloud SQL/GCS default encryption | Keep |
| At rest (field) | AES-256-GCM, single static key, tokens only; format `nonce‖tag‖ct` (`AesEncryptionService.cs:63-67`) | **v2 envelope format: `keyId‖nonce‖tag‖ct`.** Decrypt routes on `keyId` (legacy blobs = implicit `v1`); rotation = new key version + lazy re-encrypt on write. Vault payloads use per-subject DEKs wrapped by **Cloud KMS** (net-new Terraform: key ring + KEK + IAM binding to the identity service account only) |
| Immediate fix | ✅ **Done** — `MedicalNotes` is encrypted (closes §1.2), applied in `CardiMemberService` rather than as an EF value converter so pre-existing plaintext rows stay readable | Move to a value converter when a second reader of the column appears, and fold it into the v2 envelope format above |

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
- **Fix §1.8:** ✅ **Done** — `GetStatusAsync`/`DownloadAsync` verify `requestingUserId` owns the report.
- **DB roles (net-new):** `app_rw` (clinical, no `pii.*`), `identity_svc` (pii only), `app_append` (compliance INSERT), `retention_svc` (DELETE/UPDATE where the policies need it). Runtime service accounts are now partially split: API, Web, the pipeline jobs, and the webhook receiver run under dedicated SAs; **Worker, the migrator, and the aggregator still share the default compute SA** — that remaining split is the open item.
- **The application layer cannot reach the re-identification keys:** KMS `decrypt` on the vault KEK and the `export_salt` is IAM-granted only to the identity service path / export job SA. A compromised API pod can read clinical GUIDs but can neither query `pii.*` (no grant) nor unwrap DEKs (no KMS binding).

### 7.4 Third-party egress controls

- **Gemini (§1.7): stop sending the name immediately** — replace `## Patient: {member.Name}` with a neutral label; re-insert the display name into the rendered report *after* the LLM call, inside our estate. ✅ **Done** — `ReportGenerationService` labels members positionally and restores names after the response returns; the chat prompt carries readings only. The API key has also moved from the query string to the `x-goog-api-key` header (`GeminiClient.cs`), keeping the credential out of proxy and access logs. Follow-up decision (D6) is still open: move report/chat generation to Vertex AI under the Cloud BAA/DPA, or route to the in-VPC MedGemma. Both are now configuration rather than code — the public provider is selected by `AI__Public__Kind` with an optional `AI__Public__BaseUrl` override.
- **Telemetry:** Serilog enricher + OTel processor that replaces `/cardimembers/{guid}` path segments with `/cardimembers/{redacted}` before shipping; exception messages scrubbed against the same rule. Configure provider-side retention (30 d ⚖).
- **Mobile RUM:** ✅ **Resolved by removal** (PR #185). RUM is no longer enabled, so no sessions, session sampling, or Datadog-side crash reports are collected from mobile at all — the `SessionSampleRate = 100` concern no longer exists. RUM was removed because this org's Datadog site (UK1) has no member in either the `Datadog.Maui` or native `dd-sdk-android` site enum, and the `CustomEndpoint` escape hatch is inert in the only published package version, so RUM events were never deliverable. **Residual, still open:** `TrackingConsent.Granted` is still hardcoded for mobile **logs and traces**, which have no in-app opt-out — a materially smaller surface than session-level RUM capture, but the same consent question. Replace with `Pending` until the user consents in-app before any store review requiring opt-in analytics consent.
- **Push (shipped):** the design rule held — FCM/APNs payloads carry only `{alertId, severity}`; the app fetches content over authenticated API. Push infrastructure therefore never needs a BAA for content.

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
  4. **Built ahead of this framework (2026-08-12):** environmental-context enrichment's `CardiMember.EnvironmentalContextConsentGranted` flag is checked at the top of `EnvironmentalEnrichmentService.EnrichDueSessionsAsync` — its candidate query is the only code path that ever reaches location data, so this one flag is a complete, if narrow, enforcement point rather than a partial one. It predates `ConsentRecords` and does not migrate into it automatically if/when that framework ships; that migration is future work, not implied by this note.
- **Withdrawal side-effects:** withdrawal of a metric stops sync + processing of that metric immediately; withdrawal of everything triggers the disconnect flow (token revoke) and offers erasure (§6) — withdrawal of consent and erasure are separate GDPR rights and remain separate actions.

---

## 9. Subprocessor register

Everywhere data leaves the primary Postgres/VPC boundary, with the paperwork each requires **before** PHI/personal data may flow. "Blocked" = data flows today without the paperwork.

| Party | Data leaving | HIPAA | GDPR | Status / action |
|-------|-------------|-------|------|-----------------|
| **Google Cloud** (Cloud SQL, GCS, Secret Manager, KMS, Cloud Run) | All stored data | BAA available — **execute it** and confirm each service is on Google's HIPAA-covered list | Cloud Data Processing Addendum (auto-incorporated) + SCCs; region already EU (`infrastructure/main.tf`) | ⚠ Execute BAA (D1 determines whether required) |
| **Google Vertex AI** (`{region}-aiplatform.googleapis.com`, Gemini models — supersedes the consumer Gemini API row, D6 resolved 2026-08-21) | Public slot (`AI:Public`): report/chat prompts, identifiers stripped per §7.4; Rewrite slot (`AI:Rewrite`): caregiver free-text question + MedGemma's de-identified clinical read — never the member's name, id, MedicalNotes or questionnaire answers (the name travels as the literal placeholder `CardiTrackCardiMember`, substituted in only after the call returns, and swapped back out of recalled chat history before it re-enters a prompt — `NamePlaceholder.Resolve`/`Redact`; the return direction was added 2026-08-21 to close a leak via persisted turns, DPIA row A20) | BAA-eligible — Vertex AI is on Google's HIPAA-covered services list; execute with the Cloud BAA (D1) | Cloud Data Processing Addendum + SCCs (same umbrella as the Google Cloud row); **EU regional endpoint only** — the location is a validated allowlist (`europe-west2`/`west1`/`west4`) in Terraform, never the global endpoint; ZDR configuration required: project data caching disabled + abuse-monitoring prompt-logging opt-out ([vertex_ai_setup.md](./vertex_ai_setup.md)) | ⚠ **Dev's Rewrite slot flipped 2026-08-21** (`gemini-2.5-flash-lite` then, `europe-west1`); the 2026-08-25 owner decision pinned the 3.5 generation estate-wide, but the rewrite slot's `gemini-3.5-flash-lite` pin shipped without the §3 probe it required and 404'd in every EU-allowlisted region once applied (2026-08-29) — Dev's `pipeline-jobs` failed on `generate_structured` for about a day before catch. **Reverted 2026-08-30** to `gemini-3.5-flash` / `europe-west2`, confirmed served, see [vertex_ai_setup.md](./vertex_ai_setup.md) §3. Public slot (`gemini-3.5-flash`, both environments) is unaffected. **ZDR: cache disable done and evidenced (owner, 2026-08-21 — `disableCache: true` verified, see [vertex_ai_setup.md](./vertex_ai_setup.md) §2); the abuse-monitoring logging exception was filed 2026-08-21 (self-serve billing confirmed) and **approved by Google 2026-08-28** (email evidence in [vertex_ai_setup.md](./vertex_ai_setup.md) §2 item 2) — the ZDR configuration is complete. Public slot and prod are flipped in configuration (2026-08-25), unapplied. Before prod: confirm the DPA/BAA reference here. The old `generativelanguage.googleapis.com` path is removed once every environment runs the Vertex kinds |
| **Google Health API** | Inbound wearable data; outbound: OAuth tokens, revocations | Google here is the wearer-authorized **source**, not our subprocessor; restricted-scope verification + CASA gates production ([oauth_clients.md](./oauth_clients.md)) | Independent-controller relationship; disclose in privacy notice | On track (verification pending) |
| **Auth0 (Okta)** | Caregiver emails, names, Auth0UserIds | PII not PHI, but sits in the auth path of a health app — BAA available on suitable plan | DPA + SCCs (Okta standard) | ⚠ Execute DPA; confirm plan tier |
| **Datadog / Better Stack** (APM, logs, traces) | Request paths w/ member GUIDs, exceptions, DB spans. **No RUM sessions and no session replays** — RUM removed in PR #185, and Session Replay was never enabled | Pseudonymous identifiers linked to a health service ⇒ treat as PHI-adjacent; Datadog offers BAAs; **Better Stack: verify or drop for prod** | DPA + SCCs; retention config; mobile logs/traces still ship under hardcoded consent (§7.4) | 🔴 Scrub + consent-gate first; paperwork per D6 |
| **HuggingFace** | Nothing (model weights inbound only) | — | — | OK |
| ~~**Microsoft Azure** (planned AI pipeline: Functions, Event Hubs, Cosmos, Blob, Notification Hubs, ACA)~~ | ~~Readings, inference results, digests~~ | — | — | **Superseded** — the AI pipeline shipped on GCP (Pub/Sub + Cloud Run, [llm_design.md](../llm_design.md)); no Azure service processes CardiTrack data |
| **Stripe** (planned) | Payment data only — subscription metadata must never reference health status | No BAA needed if boundary holds (document it) | DPA (standard) | Design rule |
| **Twilio / Azure Communication Services** | — | — | — | **Not applicable** — SMS is permanently out of scope by decision; no SMS channel exists or is planned |
| **FCM / APNs** (push — **in use**) | Token + content-free payload (§7.4 rule) | No BAA needed while payloads stay content-free — **verified: the shipped sender's payloads are content-free** | Disclose in notice | ✅ In use, content-free payloads verified |
| **Google Maps Platform** (Weather + Air Quality APIs, built 2026-08-12) | A GPS coordinate + timestamp per consented member's exercise session, for the duration of one outbound lookup only — never logged, never stored, never returned to any caller past the enrichment call | Coordinates are not health data on their own, but pairing them with a cardiac-monitoring context makes the *disclosure itself* worth this row; the API returns only derived weather/AQI values, which are not PHI | Personal data (location) while in flight to the API; DPA status unconfirmed — same Google Cloud umbrella as other Google services but a distinct product, so it does not inherit the Cloud BAA row above | ⚠ **Blocked as used** until the DPA question is resolved — same posture as the Gemini row: the mechanism ships gated (consent flag default `false`, no scope granted yet), but production traffic should not flow before this is closed |

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
| **D6** | Third-party AI + telemetry: ~~approve Vertex-AI-with-BAA vs. in-VPC-only for LLM features~~ — **AI half resolved 2026-08-21 (owner): Vertex AI selected** for the Public and Rewrite slots (EU regional endpoints, ZDR configuration, §9 row); the clinical slot stays in-VPC. Still open: approve (or replace) Better Stack; classify APM data for DSAR/erasure purposes | Vendor risk & contracts |
| **D7** | Sign off that Tier 3 output (Safe Harbor + k-anonymity gate) is treated as **anonymous under GDPR** given the residual-means test (Recital 26) — or keep treating it as personal data | Anonymity threshold judgment |
| **D8** | If daily-granularity research/analytics data is wanted: commission **Expert Determination** (§164.514(b)(1)) — Safe Harbor cannot produce it | Requires certified expert engagement |
| **D9** | Ratify k = 5, the quasi-identifier set, and per-batch salt rotation (vs. stable longitudinal IDs) in §4.3 | Risk-appetite threshold |
| **D10** | International transfers: confirm SCC/DPF coverage for each US-headquartered processor (Auth0/Okta, Datadog, Stripe, Twilio) given EU-resident data | Transfer-mechanism selection |

---

## 11. Implementation phases

| Phase | Contents | Depends on |
|-------|----------|------------|
| **P0 — defect fixes** (no schema change) | Encrypt `MedicalNotes` (§7.1); strip name from Gemini prompts + key-to-header (§7.4); report ownership check (§1.8); telemetry GUID scrubbing; mobile telemetry consent gate for logs/traces (§7.4 — the RUM half is closed, resolved by removal in PR #185); drop dead `PasswordHash` column (`Encryption:IV` config — done); API `UseHsts()` | — |
| **P1 — audit + consent** | `PhiAuditSaveChangesInterceptor` + `PhiReadAuditFilter` writing `AuditLogs`; `compliance.consent_records` + endpoint + sync-gate enforcement; resolve the 90-day/6-year audit retention conflict (Terraform + entity comment → 6 y) | — |
| **P2 — retention** | `RetentionOptions` + `DataRetentionWorker` with advisory lock; advisory-lock + error-boundary hardening of `CronBackgroundService`; audit archive to bucket-lock GCS; retention_runs evidence table | P1 (audit) |
| **P3 — erasure + DSAR** | `SubjectDataMap` + architecture test; `erasure_requests`/`erasure_ledger`; `ErasureWorker`; DELETE + export endpoints; Auth0 delete + Google token revoke; post-restore replay hook | P1, D4 |
| **P4 — identity vault** | `pii` schema, per-subject DEKs via Cloud KMS (Terraform), `IIdentityVault` + DB roles/SA split; `CardiMembers` slimming migration (`BirthYear`/`IsOver89`); envelope-versioned `AesEncryptionService` v2 | P1; crypto-shred step of P3 upgrades automatically |
| **P5 — Tier 3 analytics** | `SafeHarborDeidentifier` + fail-closed policy tests; `KAnonymityGate`; export manifests; KMS-held `export_salt` | P4, D7/D9 |
| Gate | AI pipeline ([llm_design.md](../llm_design.md)) ships only after: Microsoft BAA (if D1 requires), Cosmos TTLs, consent gate in aggregator, erasure-by-partition wired into `SubjectDataMap` | P3 |

---

*Prepared as an architecture decision record. File issues per phase; each phase lands as an independent PR series.*
