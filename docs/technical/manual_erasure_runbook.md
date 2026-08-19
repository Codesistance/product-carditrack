# Manual Erasure Runbook

**Status: operational procedure. This is how the published 30-day deletion promise is kept until an erasure endpoint exists.**

Last updated: 2026-08-18

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

**Derive the list, do not trust this table alone.** Before starting, enumerate every table carrying a `CardiMemberId` or `UserId` column and reconcile it against the list below. New subject-linked tables get added by ordinary feature work and will not announce themselves here — this is the same failure the `SubjectDataMap` exists to remove, and until that ships the query below is the map:

```sql
SELECT table_name, column_name
FROM information_schema.columns
WHERE column_name IN ('CardiMemberId', 'UserId')
ORDER BY table_name;
```

Table names are **not** always the entity name — the questionnaire entity lives in `MemberQuestionnaires`, not `Questionnaires`. Take names from `ToTable(...)` in the persistence configuration, not from the domain class.

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
| 8 | `DigestEntries` | Digests **and Daybook entries** — partition-dropped at 90 days |
| 9 | `EnvironmentalReadings` | Feature is inert, so normally empty — check anyway |
| 10 | `GranularMetricHours` | Minute-grain; partition-dropped at 90 days |
| 11 | `MetricRollupsHourly` | Hour-grain; partition-dropped at 13 months |
| 12 | `DeviceActivityLogs` | **Raw per-device rows.** Easy to miss — `ActivityLogs` is the merged view, this is the source |
| 13 | `ActivityLogs` | The primary daily store. **No partition drop covers this table** — retained indefinitely unless deleted here |
| 14 | `MemberQuestionnaires` | Question text and free-text answers, AES-256-GCM encrypted at rest |
| 15 | `DeviceConnections` | Revoke upstream **before** deleting the row, or the token is orphaned at Google rather than revoked |
| 16 | `UserCardiMembers` | Cascades, but delete explicitly so the count is verifiable |
| 17 | `CardiMembers` | Emergency contacts, medical notes **and the profile-photo object name** live on this row |

**Profile photo blob (GCS) — not a table, easy to miss.** The member's profile photo lives outside Postgres, in the private member-photos bucket, under `members/<cardiMemberId>/`. The app hard-deletes the blob on normal member removal, but an erasure must not trust that: delete the member's whole prefix explicitly (before or after the table sweep — nothing references it):

```
gcloud storage rm gs://<member-photos-bucket>/members/<cardiMemberId>/ --recursive
```

The bucket name is environment-specific (dev: `carditrack-490120-carditrack-dev-member-photos`) — take it from the `Storage__MemberPhotos__Bucket` env var on the API service. A "matched no objects" result is fine (member never had a photo, or removal already deleted it); include the command output in the verification record either way.

### Account-scoped (`UserId`) — full closure only

| Order | Table | Notes |
|---|---|---|
| 18 | `PushDeviceTokens` | Encrypted tokens; the designed 30-day post-disable hard delete is **not enforced** |
| 19 | `NotificationPreferences` | Quiet hours, per-category mutes |
| 20 | `Subscriptions` | Keyed on `OrganizationId` |
| 21 | `Organizations`, `Users` | Retain billing records for 6 years per UK tax law — see policy §5 |

Reports are cached with a 1-hour TTL and generated fire-and-forget in-process, so there is no durable report table to clear.

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
