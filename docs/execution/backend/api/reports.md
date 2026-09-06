# Reports API

Handles async generation and download of health summary reports for doctor visits. Report generation is asynchronous.

**Implementation status:** all three endpoints are **implemented**, and **PDF, CSV and FHIR R4 render for real** (MVP 1). HL7 v2 is **MVP 2** and is rejected at validation rather than accepted and silently ignored.

How generation works:

- Generation is **fire-and-forget in-process** (`Task.Run` inside the API) — there is still no durable queue — but it opens **its own DI scope**, because the request's `IUnitOfWork` is disposed the moment the 202 goes out.
- **State is durable.** A `Reports` row carries the request and its outcome; the rendered bytes live in the **health-data export GCS bucket** (`Storage:Reports:Bucket`), per [infrastructure.md](../../../infrastructure.md)'s rule that files never live in the database. A restart no longer loses in-flight or completed reports.
- **Retention is 7 days** (`Storage:Reports:Retention`), enforced by `ExpiredReportCleanupWorker` in `CardiTrack.Worker`, with a slacker GCS lifecycle rule as the backstop. The same worker fails out reports left `Pending` past `Storage:Reports:GenerationTimeout` (15 min) — the abandoned generations the old 1-hour cache TTL used to hide.
- Report IDs are GUIDs in compact **`"N"` format** (32 hex chars, no dashes). The dashed form is also accepted on read.
- **Ownership is checked up front**: `ReportGenerationService.GenerateAsync` calls `RequireViewAccessAsync` on every requested CardiMember ID before queueing — any id the caller cannot read fails the **whole request with 404** (indistinguishable from a nonexistent member).
- **Plan-gated**: `POST` requires **Complete Care or above** via `IEntitlementService`, and refuses with **402** naming the tier needed. The status and download endpoints are **deliberately ungated** — a plan that lapses after generation must not strip a caregiver of a record they already asked for.
- **Business validation** now exists (`GenerateReportValidator`): **max 5 CardiMembers**, **max 365-day range**, no duplicate members, at least one section, and an MVP 1 format.
- **Privacy:** the **AI narrative is generated only for PDF**. Because it goes to the public Gemini endpoint, member names are pseudonymised as "Patient A", "Patient B", … before the model call and swapped back only after the response returns. The model never sees a real name. **CSV and FHIR R4 make no model call at all.**
- **No free text crosses into any export** — no medical notes, no alert message bodies, no caregiver device labels ([data_protection_architecture.md](../../../technical/data_protection_architecture.md) §70, §85).

**User Stories:** 2.3 (Trend Charts & Historical Data — export), 6.3 (Health Data Export), 9.2 (Printable Reports)

---

## POST `/api/v1/reports`

Queue async generation of a health summary report for one or more CardiMembers. Returns a report ID to poll. (There is no `/generate` suffix.)

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
  "includeNotes": false,
  "includeDevices": false,
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
| `includeTrends` | boolean | No | Default `true`; currently has no effect |
| `includeAlerts` | boolean | No | Include alert history in range (default `true`) |
| `includeNotes` | boolean | No | Default `false`; no notes feature exists |
| `includeDevices` | boolean | No | Include device provenance — device **types** only, never caregiver labels (default `false`) |
| `title` | string | No | Rendered onto the PDF cover; ignored by CSV and FHIR |

`GenerateReportValidator` enforces the rules above. At least one of `includeMetrics` / `includeAlerts` / `includeDevices` must be true.

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
| 400 | A business rule failed — too many members, a range over 365 days, duplicate members, no sections, or HL7 v2 |
| 402 | The organisation's plan does not include export (Basic). The message names the tier needed |
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

- **PDF** — the AI narrative (labelled as AI-generated), then a daily table per member, then alerts. A confidentiality footer and page numbers on every page, because printed pages get separated. A reading the device never reported prints as an em dash, never a zero.
- **CSV** — one row per member per day for the daily metrics, then an alerts block, then a devices block, separated by blank lines. UTF-8 **with a BOM** (without it Excel on Windows mangles non-ASCII names); invariant numbers and ISO dates. A missing reading is an empty cell, never a zero.
- **FHIR R4** — a `collection` `Bundle` of `Patient`, `Device` and one `Observation` per metric per day, LOINC-coded with UCUM units, every resource labelled `R` (restricted). Resource ids are real GUIDs, because `urn:uuid:` is a registered scheme and a strict parser rejects anything else. A reading with no agreed LOINC code is omitted rather than given an invented one.

> **Still not implemented:** HL7 v2 (MVP 2, rejected at validation), LOINC/CCD (MVP 2), SNOMED CT (MVP 3), and `X-HIPAA-Confidential` response headers.

### Errors

| Status | When |
|--------|------|
| 404 | Report unknown, expired, owned by another user, or its object is gone from the bucket — **410 is never returned** |
| 409 | Report exists but is not `Ready` yet (still pending, or failed) |

---

**Related:** [readme.md](readme.md) | [health-data.md](health-data.md) | [User Stories 2.3, 9.2](../../ui/mobile/user_stories.md)

**Last Updated:** September 6, 2026
