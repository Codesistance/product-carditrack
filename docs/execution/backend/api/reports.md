# Reports API

Handles async generation and download of health summary reports for doctor visits. Report generation is asynchronous.

**Implementation status:** all four endpoints are **implemented**, and **PDF, CSV and FHIR R4 render for real** (MVP 1). HL7 v2 is **MVP 2** and is rejected at validation rather than accepted and silently ignored. Every generate call must present a short-lived consent token from `POST /api/v1/reports/consent`.

How generation works:

- Generation is **fire-and-forget in-process** (`Task.Run` inside the API) — there is still no durable queue — but it opens **its own DI scope**, because the request's `IUnitOfWork` is disposed the moment the 202 goes out.
- **State is durable.** A `Reports` row carries the request and its outcome; the rendered bytes live in the **health-data export GCS bucket** (`Storage:Reports:Bucket`), per [infrastructure.md](../../../infrastructure.md)'s rule that files never live in the database. A restart no longer loses in-flight or completed reports.
- **Retention is 7 days** (`Storage:Reports:Retention`), enforced by `ExpiredReportCleanupWorker` in `CardiTrack.Worker`, with a slacker GCS lifecycle rule as the backstop. The same worker fails out reports left `Pending` past `Storage:Reports:GenerationTimeout` (15 min) — the abandoned generations the old 1-hour cache TTL used to hide.
- Report IDs are GUIDs in compact **`"N"` format** (32 hex chars, no dashes). The dashed form is also accepted on read.
- **Ownership is checked up front**: `ReportGenerationService.GenerateAsync` calls `RequireViewAccessAsync` on every requested CardiMember ID before queueing — any id the caller cannot read fails the **whole request with 404** (indistinguishable from a nonexistent member).
- **Not plan-gated.** Nothing in CardiTrack is gated by plan today (R1 is trial-only; subscriptions ship in R2), so export is open to every signed-in caregiver. The `IEntitlementService` that gated it on 2026-09-06 was removed on 2026-09-07. When gating arrives, the read paths (status, download) should stay ungated — a plan that lapses after generation must not strip a caregiver of a record they already asked for.
- **Business validation** now exists (`GenerateReportValidator`): **max 5 CardiMembers**, **max 365-day range**, no duplicate members, at least one section, and an MVP 1 format.
- **Privacy:** the **AI narrative is generated only for PDF**. Because it goes to the public Gemini endpoint, member names are pseudonymised as "Patient A", "Patient B", … before the model call and swapped back only after the response returns. The model never sees a real name. **CSV and FHIR R4 make no model call at all.**
- **No caregiver free text crosses into any export** — no medical notes, no alert message bodies, no caregiver device labels ([data_protection_architecture.md](../../../technical/data_protection_architecture.md) §70, §85). Journals are CardiTrack-generated AI text and may be included on PDF/CSV when ticked; each entry is labelled as AI. Notices export `RuleCode` / category / state, never localized `TitleKey` bodies.
- **Recorded consent.** The password is verified on the device against Auth0 and is **never sent to CardiTrack**. The API records only that the caregiver accepted responsibility and which proof they used (`Password` or `Biometric`). The generate token is single-use, owner-scoped, bound to the request fingerprint, and expires in two minutes. On the consent prompt the caregiver may also keep that confirmation for **1 week, 2 weeks, or 1 month** (30 days). A standing grant authorizes later exports without another step-up; each reuse mints a fresh two-minute token, is written as its own row (so the audit trail names every export), and the API/client **always tell the caregiver the confirmation is being reused**. Settings lists every confirmation and can stop a standing grant. A changed policy hash cannot be reused — they confirm again.

**User Stories:** 2.3 (Trend Charts & Historical Data — export), 6.3 (Health Data Export), 9.2 (Printable Reports)

---

## POST `/api/v1/reports/consent`

Records that the caregiver accepted responsibility for this exact export and proved it (password or biometrics, verified on the device). Returns a compact `"N"` token the generate call must present.

**Priority:** P0 | **Auth Required:** Yes

### Request Body

The same snapshot generate will send — members, dates, format, and section flags — plus how they proved it:

```json
{
  "cardiMemberIds": ["3fa85f64-5717-4562-b3fc-2c963f66afa6"],
  "dateRangeFrom": "2026-07-07",
  "dateRangeTo": "2026-08-07",
  "format": 1,
  "includeMetrics": true,
  "includeTrends": true,
  "includeAlerts": true,
  "includeJournals": true,
  "includeNotices": true,
  "includeDevices": false,
  "method": 1,
  "acceptedResponsibility": true
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| snapshot fields | — | Yes | Same ceilings as generate (members, range, format, at least one section) |
| `method` | integer enum | Yes | `ExportConsentMethod`: Password=1, Biometric=2 |
| `acceptedResponsibility` | boolean | Yes | Must be `true` — the client only sends this after the responsibility popup |
| `rememberFor` | integer enum | No | `ExportConsentRememberFor`: ThisExport=1 (default), OneWeek=2, TwoWeeks=3, OneMonth=4 (30 days). A value other than ThisExport keeps a standing grant the next export may reuse |

### Response `200 OK`

```json
{
  "consentToken": "8f14e45fceea167a5a36dedd4bea2543",
  "expiresAt": "2026-08-07T10:02:00Z",
  "reused": false,
  "rememberUntil": null
}
```

---

## POST `/api/v1/reports/consents/{consentId}/reuse`

Mints a two-minute generate token from the named in-force standing grant for this snapshot. **404** when that grant cannot be reused (unknown, not theirs, expired, revoked, a reuse child rather than a standing grant, or the policy text has changed) — the client then runs the full confirmation. The envelope **message** and `reuseNotice` always say that an earlier confirmation is being reused. Binding reuse to `{consentId}` keeps the disclosure the caregiver saw aligned with the grant that actually mints the token.

**Priority:** P0 | **Auth Required:** Yes

### Request Body

The same snapshot generate will send (members, dates, format, section flags). `consentToken` is ignored.

### Response `200 OK` (wrapped in `ApiResponse<T>`)

```json
{
  "success": true,
  "message": "We're using the confirmation you gave on 1 Aug 2026. It stays in force until 15 Aug 2026. You can stop this in Settings.",
  "data": {
    "consentToken": "c4ca4238a0b923820dcc509a6f75849b",
    "expiresAt": "2026-08-07T10:02:00Z",
    "reused": true,
    "reusedFromConsentId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "originalConsentedAt": "2026-08-01T10:00:00Z",
    "rememberUntil": "2026-08-15T10:00:00Z",
    "reuseNotice": "We're using the confirmation you gave on 1 Aug 2026. It stays in force until 15 Aug 2026. You can stop this in Settings."
  },
  "timestamp": "2026-08-07T10:00:00Z"
}
```

---

## GET `/api/v1/reports/consents`

Every confirmation this caregiver has given, newest first. Settings uses this list: standing grants with `canRevoke` can be stopped; `canReuse` is the grant the next export will offer to reuse.

**Priority:** P1 | **Auth Required:** Yes

### Response `200 OK` (wrapped in `ApiResponse<T>`)

```json
{
  "success": true,
  "message": "",
  "data": [
    {
      "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
      "recordedAt": "2026-08-01T10:00:00Z",
      "method": 2,
      "rememberFor": 3,
      "rememberUntil": "2026-08-15T10:00:00Z",
      "revokedAt": null,
      "consumedAt": "2026-08-01T10:00:20Z",
      "reused": false,
      "canRevoke": true,
      "canReuse": true,
      "summary": "In force until 15 Aug 2026 · fingerprint or face unlock"
    }
  ],
  "timestamp": "2026-08-07T10:00:00Z"
}
```

---

## DELETE `/api/v1/reports/consents/{consentId}`

Stops a standing grant. Later exports must confirm again. Copies already made are unchanged. **404** when the id is unknown, not theirs, or is not a standing grant they can stop.

**Priority:** P1 | **Auth Required:** Yes

### Response `200 OK`

Envelope message: `"That confirmation is no longer in force."`

---

## POST `/api/v1/reports`

Queue async generation of a health summary report for one or more CardiMembers. Returns a report ID to poll. (There is no `/generate` suffix.) A generate without a matching unused consent token is **400**.

**Priority:** P0 | **Auth Required:** Yes

### Request Body

Flat shape — date range and section toggles are **top-level fields**, not nested objects:

```json
{
  "cardiMemberIds": ["3fa85f64-5717-4562-b3fc-2c963f66afa6"],
  "dateRangeFrom": "2026-07-07",
  "dateRangeTo": "2026-08-07",
  "format": 1,
  "fhirProfile": "us-core",
  "fhirResources": ["Patient", "Observation", "Device"],
  "includeMetrics": true,
  "includeTrends": true,
  "includeAlerts": true,
  "includeJournals": true,
  "includeNotices": true,
  "includeNotes": false,
  "includeDevices": false,
  "consentToken": "8f14e45fceea167a5a36dedd4bea2543",
  "title": "Health Summary for Dr. Smith Visit"
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `cardiMemberIds` | GUID array | Yes | 1–**5** CardiMember IDs, no duplicates. Each is ownership-checked (`RequireViewAccessAsync`) — one unreadable id fails the request with **404** |
| `dateRangeFrom` | date (`DateOnly`) | Yes | Start date. The range may span at most **365 days** |
| `dateRangeTo` | date (`DateOnly`) | Yes | End date |
| `format` | integer enum | Yes | `ReportFormat`: Pdf=1, Csv=2, FhirR4=3, Hl7V2=4. **Pdf/Csv/FhirR4 render; Hl7V2 is rejected with 400** (MVP 2) |
| `fhirProfile` | string | No | Default `"us-core"`, which is the shape the FHIR renderer emits. Other values are not yet honoured |
| `fhirResources` | string array | No | Default `["Patient", "Observation", "Device"]` — the three the bundle carries. Not yet used to narrow the bundle |
| `includeMetrics` | boolean | No | Include daily activity metrics (default `true`) |
| `includeTrends` | boolean | No | Default `true`. On PDF, draws line charts for the selected days (gaps for missing days, never zeros). Ignored by CSV and FHIR |
| `includeAlerts` | boolean | No | Include alert history in range (default `true`) |
| `includeJournals` | boolean | No | Include Daybook / Weekbook / Monthbook entries in range (default `false`). The live Family glance is never exported |
| `includeNotices` | boolean | No | Include the completeness inbox (stale device, battery, …) first-detected in range (default `false`) |
| `journalEntryDate` / `journalAudience` | date / enum | No | When both are set, journals are scoped to that one entry rather than every book in the range. `journalAudience` must be Daybook / Weekbook / Monthbook (`Family` and `Wearer` are 400). A pinned day must fall inside `dateRangeFrom`–`dateRangeTo`. Either field without `includeJournals` is 400 |
| `includeNotes` | boolean | No | Default `false`; no notes feature exists |
| `includeDevices` | boolean | No | Include device provenance — device **types** only, never caregiver labels (default `false`) |
| `consentToken` | string | Yes | Token from `POST /api/v1/reports/consent` for this same snapshot |
| `title` | string | No | Rendered onto the PDF cover; ignored by CSV and FHIR |

`GenerateReportValidator` enforces the rules above. At least one of `includeMetrics` / `includeAlerts` / `includeDevices` / `includeJournals` / `includeNotices` must be true, and for `format: 3` (FHIR R4) at least one of `includeMetrics` / `includeDevices` must be true — journals and notices are PDF/CSV only. See the FHIR note under the download endpoint.

### Response `202 Accepted` (wrapped in `ApiResponse<T>`)

```json
{
  "success": true,
  "message": "We're preparing your report — it'll be ready shortly!",
  "data": {
    "reportId": "8f14e45fceea167a5a36dedd4bea2543",
    "status": 1,
    "estimatedReadyInSeconds": 30,
    "statusUrl": "/api/v1/reports/8f14e45fceea167a5a36dedd4bea2543"
  },
  "timestamp": "2026-08-07T10:00:00Z"
}
```

`status` is the integer `ReportStatus` enum: Pending=1, Ready=2, Failed=3, Expired=4.

### Errors

| Status | When |
|--------|------|
| 400 | A business rule failed — too many members, a range over 365 days, duplicate members, no sections, HL7 v2, a missing/expired/mismatched consent token |
| 404 | A requested CardiMember ID is unknown **or not readable by the caller** — deliberately indistinguishable |

---

## GET `/api/v1/reports/{reportId}`

Check the status of an in-progress or completed report.

**Priority:** P1 | **Auth Required:** Yes

> **Owner-scoped:** the `Reports` row stamps `OwnerUserId` at generation, and both status and download return **404 for anyone but the requesting user** — indistinguishable from an expired report, so a stolen report ID discloses nothing, not even that the report exists.

### Response `200 OK` — Ready (wrapped in `ApiResponse<T>`)

```json
{
  "reportId": "8f14e45fceea167a5a36dedd4bea2543",
  "status": 2,
  "progressPercent": null,
  "format": 1,
  "contentType": "application/pdf",
  "fileSizeBytes": 48210,
  "downloadUrl": "/api/v1/reports/8f14e45fceea167a5a36dedd4bea2543/download",
  "downloadExpiresAt": "2026-08-14T10:00:00Z",
  "createdAt": "2026-08-07T10:00:00Z",
  "completedAt": "2026-08-07T10:00:24Z",
  "error": null,
  "metadata": {
    "cardiMembers": ["3fa85f64-5717-4562-b3fc-2c963f66afa6"],
    "dateRangeFrom": "2026-07-07",
    "dateRangeTo": "2026-08-07",
    "sections": null,
    "fhirProfile": null,
    "fhirResources": null
  }
}
```

Contract notes (verified against `ReportGenerationService`):

- `progressPercent` is **always `null`** — no progress tracking exists.
- `format` is the integer `ReportFormat`, echoed from the request and populated from the moment the report is queued.
- `contentType` and `fileSizeBytes` are `null` until the report is `Ready`; `downloadUrl` is only present when it is.
- `downloadExpiresAt` is stamped at **queue** time, so a slow generation cannot shorten the window the caregiver was told about.
- `metadata.cardiMembers` contains **GUID strings**, not member names.
- Date-range fields are **flat** (`dateRangeFrom`/`dateRangeTo`), not a nested `dateRange` object.
- On failure, `status` is 3 and `error` is a generic "Report generation failed. Please try again."

**Report Status Values** (integer `ReportStatus` enum):

| Value | Name | Description |
|-------|------|-------------|
| 1 | `Pending` | Generation queued or in progress |
| 2 | `Ready` | Report generated and available for download |
| 3 | `Failed` | Generation failed — see `error` field |
| 4 | `Expired` | **Never assigned in practice** — expiry manifests as a 404 once `ExpiresAt` has passed, not this status |

### Errors

| Status | When |
|--------|------|
| 404 | Report ID unknown, **past the 7-day window** ("it may have expired"), or owned by another user |

---

## GET `/api/v1/reports/{reportId}/download`

Download the generated report. **The download window is 7 days** from generation (`Storage:Reports:Retention`), stamped at queue time.

**Priority:** P1 | **Auth Required:** Yes (owner-scoped, same as the status endpoint — anyone else gets 404). **Not plan-gated** — see the implementation status above.

### Response `200 OK`

Content type and filename follow the requested format:

| Format | Content-Type | Filename |
|--------|--------------|----------|
| PDF | `application/pdf` | `carditrack-export-margaret-doe-20260207-20260309.pdf` |
| CSV | `text/csv; charset=utf-8` | `…-20260207-20260309.csv` |
| FHIR R4 | `application/fhir+json` | `…-20260207-20260309.json` |

The filename's subject is a slug of the member's name for a single-member export, or `{n}-members` past that. It is ASCII letters, digits and hyphens only — it reaches a `Content-Disposition` header.

**Bytes are streamed through the API, never redirected to a signed bucket URL.** A signed URL would be a bearer capability to a complete identified health record: outside the ownership check, and invisible to the `[AuditHealthDataAccess]` row this request writes.

What each format contains:

- **PDF** — the AI narrative (labelled as AI-generated), then age and sex (not date of birth), optional trend charts for the selected days, a daily table per member, then alerts, journals (each labelled as AI), and notices. A confidentiality footer and page numbers on every page, because printed pages get separated. A reading the device never reported prints as an em dash, never a zero.
- **CSV** — one row per member per day for the daily metrics, then an alerts block, then a devices block, then journals and notices when ticked, separated by blank lines. UTF-8 **with a BOM** (without it Excel on Windows mangles non-ASCII names); invariant numbers and ISO dates. A missing reading is an empty cell, never a zero.
- **FHIR R4** — a `collection` `Bundle` of `Patient`, `Device` and one `Observation` per metric per day, LOINC-coded with UCUM units, every resource labelled `R` (restricted). Resource ids are real GUIDs, because `urn:uuid:` is a registered scheme and a strict parser rejects anything else. A reading with no agreed LOINC code is omitted rather than given an invented one. **Alerts are not in the bundle in MVP 1** — they are CardiTrack's own statistical findings, and `DetectedIssue`, `Flag` and an `Observation` of the triggering reading each imply a different clinical meaning to the receiving system. A FHIR request whose only selected section is alerts is **refused with 400** rather than answered with a lone `Patient`; ticking alerts alongside readings is accepted and the readings are returned.

> **Still not implemented:** HL7 v2 (MVP 2, rejected at validation), LOINC/CCD (MVP 2), SNOMED CT (MVP 3), and `X-HIPAA-Confidential` response headers.

### Errors

| Status | When |
|--------|------|
| 404 | Report unknown, expired, owned by another user, or its object is gone from the bucket — **410 is never returned** |
| 409 | Report exists but is not `Ready` yet (still pending, or failed) |

Both messages are **fixed caregiver-facing copy** and never echo the requested id or name the internal `ReportStatus`. The four 404 causes share one message, because they are meant to be indistinguishable.

---

**Related:** [readme.md](readme.md) | [health-data.md](health-data.md) | [User Stories 2.3, 9.2](../../ui/mobile/user_stories.md)

**Last Updated:** September 11, 2026
