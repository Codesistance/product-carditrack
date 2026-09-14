# Manual Erasure Runbook

**Status: operational procedure. This is how the published 30-day deletion promise is kept until an erasure endpoint exists.**

Last updated: 2026-09-13

## Why this document exists

[privacy-policy](https://carditrack.com/privacy-policy) §5 and the [account deletion page](https://carditrack.com/delete-account) both commit to completing deletion **within 30 days of a verified request**, and confirming by email. The Google Health API section of the policy makes the same commitment for data collected under Google scopes, which makes it a commitment to Google as well as to the data subject.

No code delivers this. Per [data_protection_architecture.md](./data_protection_architecture.md) findings 5 and 6, and [dpia.md](../compliance/dpia.md):

- There is **no account-deletion endpoint** and no erasure endpoint of any kind.
- `CardiMemberService.RemoveAsync` and `DeviceConnectionService.DisconnectAsync` are **soft deletes** — they flip `IsActive` and discard OAuth tokens. No PHI row is removed.
- The schema is deliberately almost free of foreign keys. Only `UserCardiMembers` and `Subscriptions` cascade. Deleting a CardiMember **orphans** its `ActivityLogs`, `Alerts`, `PatternBaselines`, `DeviceConnections` and `AuditLogs` rows, which stay live and queryable.

So the promise is not impossible — at current scale (under 100 connected wearers, capped until Google verification passes) it is a manual database procedure. It is only undocumented, which is worse: an unwritten obligation gets forgotten, and an unmet published deletion promise is the failure mode the FTC actions cited in [solution_manifest.md](../solution_manifest.md) Risk 3 turned on.

**This runbook is an interim control, not a solution.** The erasure endpoint is tracked in [release_matrix.md](../release_matrix.md).

## Before you start

1. **Verify the requester.** Confirm the request comes from the address on the account. If the request concerns a wearer rather than the account holder, the wearer's own rights take precedence over the caregiver's — see privacy policy §7.
2. **Record the request** — date received, requester, scope (whole account, or one CardiMember), and the 30-day due date.
3. **Stop collection first.** Revoke the device connection before deleting anything, so a sync in flight cannot re-insert rows behind you. Paused and disconnected members are already excluded from sync scheduling, and the webhook path applies the same exclusion, so a notification cannot resurrect stopped collection.
4. **Take a backup snapshot** and note its identifier. Deletion is irreversible and mistakes here are unrecoverable.

## Deletion order

Delete children before parents. There are almost no cascades, so nothing is removed for you.

**Derive the list, do not trust this table alone.** Before starting, enumerate every table carrying a `CardiMemberId` or `UserId` column — **and** the tables that reach the subject another way: a `CardiMemberIds` array (`ExportConsents`, `Reports`), an owner or requester column (`OwnerUserId`, `RequestedByUserId`), or an organisation key (`OrganizationId`, on the account-level `MetricAlarms` rows) — and reconcile it against the list below. New subject-linked tables get added by ordinary feature work and will not announce themselves here — this is the same failure the `SubjectDataMap` exists to remove, and until that ships the query below is the map:

```sql
SELECT table_name, column_name
FROM information_schema.columns
WHERE column_name IN ('CardiMemberId', 'CardiMemberIds', 'OrganizationId')
   OR column_name LIKE '%UserId'
ORDER BY table_name;
```

Table names are **not** always the entity name — the questionnaire entity lives in `MemberQuestionnaires`, not `Questionnaires`. Take names from `ToTable(...)` in the persistence configuration, not from the domain class.

**Two columns reference a caregiver without owning the row:** `Alerts.AcknowledgedByUserId` and `MemberQuestionnaires.AnsweredByUserId` (both nullable). When a caregiver's account is erased but the member stays — another caregiver remains linked — set those to null rather than deleting the member's alert or answer; the `LIKE '%UserId'` clause above is what surfaces them, so do not narrow it back to the exact names.

### Member-scoped (`CardiMemberId`) — for a single CardiMember or a full closure

| Order | Table | Notes |
|---|---|---|
| 1 | `NotificationDeliveries` | Delivery outbox rows; also carries `UserId` |
| 2 | `NotificationMutes` | Also carries `UserId` |
| 3 | `Notifications` | Also carries `UserId` |
| 4 | `AlertPreferences` | Per-member alert configuration |
| 5 | `Alerts` | Includes acknowledged and resolved rows |
| 6 | `PatternBaselines` | Append-only, roughly 1,825 rows per member per year |
| 7 | `RealtimeAssessments` | Partition-dropped at 90 days, but do not wait for it |
| 8 | `DigestEntries` | Digests **and CardiJournal entries** (Daybook, Weekbook) — partition-dropped at **7 months** |
| 9 | `EnvironmentalReadings` | Feature is inert, so normally empty — check anyway |
| 10 | `GranularMetricHours` | Minute-grain; partition-dropped at 90 days |
| 11 | `MetricRollupsHourly` | Hour-grain; partition-dropped at 13 months |
| 12 | `DeviceActivityLogs` | **Raw per-device rows.** Easy to miss — `ActivityLogs` is the merged view, this is the source |
| 13 | `ActivityLogs` | The primary daily store. **No partition drop covers this table** — retained indefinitely unless deleted here |
| 14 | `MemberQuestionnaires` | Question text and free-text answers, AES-256-GCM encrypted at rest |
| 15 | `MemberChatSessions` | Caregiver Q&A about this member — the full question and answer text per turn, plus the encrypted theme. `MemberChatTurns` and `MemberChatTurnUsages` cascade from the session (`MemberChatSessionConfiguration`), but re-query both to verify the count. Also carries `UserId`. **No partition drop and no retention worker covers these** — retained indefinitely unless deleted here |
| 16 | `MemberAdvises` | The current "Something to try" suggestion — one row per member per topic, derived from health data; overwritten on each regeneration, so this is the whole history |
| 17 | `MetricAlarmStates` | Per-member state of each custom alarm (`MetricAlarmId`, `CardiMemberId`) — a child of `MetricAlarms`, so it goes first — delete these before the alarm rows below |
| 18 | `MetricAlarms` (member rows) | Caregiver-defined alarms **tuned for this member** — rows where `CardiMemberId` is this member. Account-level rows (`CardiMemberId` null) are account-scoped, below |
| 19 | `MemberStatusLines` | The saved dashboard status sentence — derived from health data, one row per member |
| 20 | `MemberAiHolds` | The per-member, per-purpose hold the AI pipeline sets when a read fills its ceiling — records that a member's data was being assessed |
| 21 | `DeviceHistoryRepulls` | Caregiver-requested history re-pulls; carries `CardiMemberId` and `RequestedByUserId`, so it is also swept at account closure |
| 22 | `ExportConsents` (rows naming this member) | Keyed on `OwnerUserId`, but each row's `CardiMemberIds` array names the members an export covered. For a single-member erasure delete every row whose array contains the member and re-query the array; the owner-keyed sweep at account closure is below |
| 23 | `Reports` (rows naming this member) | Durable export metadata: `OwnerUserId`, a `CardiMemberIds` array and the export's `ObjectName` in the report-exports bucket. Delete every row whose array contains the member **and the object it names** (`gcloud storage rm gs://<report-exports-bucket>/<ObjectName>`; bucket from `Storage__Reports__Bucket` on the API service). `ExpiredReportCleanupWorker` sweeps rows past `ExpiresAt`, but an erasure must not wait for it |
| 24 | `DeviceConnections` | Revoke upstream **before** deleting the row, or the token is orphaned at Google rather than revoked |
| 24b | `CardiMemberCreationKeys` (rows naming this member) | Keyed on `CardiMemberId`, with no foreign key to cascade behind it. Left in place, the row outlives the member it names — and because a spent key whose member is gone makes the next retry create a fresh one, an erased member could be re-added by a stale draft the caregiver still holds. Delete by `CardiMemberId` here; the `UserId` sweep at account closure is a different pass, not a substitute |
| 25 | `UserCardiMembers` | Cascades, but delete explicitly so the count is verifiable |
| 26 | `CardiMembers` | Emergency contacts, medical notes **and the profile-photo object name** live on this row |

**Profile photo blob (GCS) — not a table, easy to miss.** The member's profile photo lives outside Postgres, in the private member-photos bucket, under `members/<cardiMemberId>/`. The app hard-deletes the blob on normal member removal, but an erasure must not trust that: delete the member's whole prefix explicitly (before or after the table sweep — nothing references it):

```
gcloud storage rm gs://<member-photos-bucket>/members/<cardiMemberId>/ --recursive
```

The bucket name is environment-specific (dev: `carditrack-490120-carditrack-dev-member-photos`) — take it from the `Storage__MemberPhotos__Bucket` env var on the API service. A "matched no objects" result is fine (member never had a photo, or removal already deleted it); include the command output in the verification record either way.

### Account-scoped (`UserId`) — full closure only

| Order | Table | Notes |
|---|---|---|
| 27 | `NotificationDeliveries` (account-level rows) | Rows where `CardiMemberId` is null — safety and nudge deliveries, the push canary; keyed on `UserId`. Rows 1–3 above only reach the member-scoped ones |
| 28 | `NotificationMutes` (account-level rows) | `CardiMemberId` null, keyed on `UserId` |
| 29 | `Notifications` (account-level rows) | `CardiMemberId` null — account notices; keyed on `UserId` |
| 30 | `PushDeviceTokens` | Encrypted tokens; the designed 30-day post-disable hard delete is **not enforced** |
| 31 | `NotificationPreferences` | Quiet hours, per-category mutes |
| 32 | `ExportConsents` (all rows) | Keyed on `OwnerUserId`; the owner's remaining consent rows, after the member-naming rows were removed above |
| 33 | `Reports` (all rows) | Keyed on `OwnerUserId`; every remaining report row and every object it names in the report-exports bucket — same command as the member row above |
| 34 | `MetricAlarms` (account rows) | Rows where `CardiMemberId` is null — the account-wide defaults, keyed on `OrganizationId` |
| 35 | `Subscriptions` | Keyed on `OrganizationId` |
| 36 | `Organizations`, `Users` | Retain billing records for 6 years per UK tax law — see policy §5 |

### Caregiver only (`UserId`) — the account closes but the members stay

The account-scoped rows above assume the members go too. When another caregiver remains linked to the members, erase **only the departing user's own rows**, keyed by their `UserId`, and leave every member-scoped table alone. If they were the *last* caregiver on a member, that member has nobody left to reach them: treat it as a full closure instead.

| Order | Table | Notes |
|---|---|---|
| 37 | `MemberChatSessions` (this user's) | Their transcripts about the members; `MemberChatTurns` and `MemberChatTurnUsages` cascade — verify both |
| 38 | `NotificationDeliveries`, `NotificationMutes`, `Notifications` (this user's) | All three carry `UserId`; the member's rows for other caregivers stay |
| 39 | `DeviceHistoryRepulls` (rows they requested) | `RequestedByUserId` is required, so these cannot be nulled — delete them; the 48-hour re-pull cooldown resets for that member |
| 40 | `Alerts.AcknowledgedByUserId`, `MemberQuestionnaires.AnsweredByUserId` | **Null, do not delete** — the alert and the answer belong to the member |
| 41 | `UserCardiMembers` (this user's links) | Delete last among the member-facing rows, so the count checks above still resolve the user |
| 41b | `CardiMemberCreationKeys` (this user's) | Keyed on `UserId`; three columns naming one creation attempt they made. Nothing depends on them, so delete outright |
| 42 | `PushDeviceTokens`, `NotificationPreferences`, `ExportConsents`, `Reports` (with their bucket objects), `Users` | The user-keyed rows from the account-scoped table; `Organizations` and `Subscriptions` stay while other users remain |

**Reports are durable.** Since the self-service export shipped (2026-09-07) every generated export has a `Reports` row and an object in the report-exports bucket, both listed above; the earlier statement here that reports were an in-process one-hour cache with no table to clear is no longer true.

**`AuditLogs` are retained, not deleted.** They are the record that the erasure happened and are needed to demonstrate compliance. This is a legitimate exception under Art. 17(3)(b), but note the unresolved conflict flagged in [dpia.md](../compliance/dpia.md): the policy implies a 6-year schedule, the deployed retention is 30/90 days, and the entity comment says 90 days. **Resolve that before quoting a figure to any data subject** — and note the deletion page currently points at "the retention schedule in the Privacy Policy" for audit logs, which has no audit-log row.

## Verification

Before confirming to the requester, re-query every table above by the same key and confirm a zero count. A deletion that leaves orphaned health rows live and queryable is exactly the gap this runbook exists to close, and orphans are invisible without an explicit check — there is no global query filter and these tables do not implement `ISoftDeletable`.

Record: tables touched, row counts removed, who performed it, and the verification timestamp.

## Backups

Residual copies persist in encrypted backups until they age out on the rotation cycle. The policy describes this accurately. Note the unresolved conflict between the 7-snapshot Cloud SQL bound and the 90-day figure quoted elsewhere — pin this down before restating it to a data subject.

## Confirm

Email the requester confirming completion, within the 30-day window. Close the request record with the completion date.

## What replaces this

An `ErasureWorker`, an erasure-request endpoint, `erasure_requests` / `erasure_ledger` tables and a `SubjectDataMap` are all designed in [data_protection_architecture.md](./data_protection_architecture.md) §§3.4, 5.2, 6.2 (phases P2/P3) but unbuilt. Until they ship, the table list above **is** the subject data map, and it must be updated whenever a new subject-linked table is added — otherwise an erasure will silently miss it.
