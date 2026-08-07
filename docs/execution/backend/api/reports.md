# Reports API

Handles async generation and download of health summary reports for doctor visits. Report generation is asynchronous.

**Implementation status:** all three endpoints below are **implemented**, but the current output is an **LLM-generated plain-text report** (MedGemma summarises the member's activity logs and alerts). PDF, CSV, FHIR R4, and HL7 v2 rendering are **planned — not yet implemented**; the `format` field is accepted and echoed but does not change the output.

How generation works today:

- Generation is **fire-and-forget in-process** (`Task.Run` inside the API) — there is no durable queue. All report state (status + content) lives in the **distributed cache** with a **1-hour TTL**; an API restart loses in-flight and completed reports.
- Report IDs are GUIDs in compact **`"N"` format** (32 hex chars, no dashes).
- **No request validation exists**: no date-range limit, no member-count cap, and **no ownership check on the requested CardiMember IDs** (unknown members are silently skipped when building the report).

**User Stories:** 2.3 (Trend Charts & Historical Data — export), 6.3 (Health Data Export), 9.2 (Printable Reports)

---

## POST `/api/v1/reports`

Queue async generation of a health summary report for one or more CardiMembers. Returns a report ID to poll. (There is no `/generate` suffix.)

**Priority:** P0 | **Auth Required:** Yes

> **Auth drift:** this controller returns **401** (not the 403 used elsewhere) when the JWT is valid but no local user row exists yet.

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
| `cardiMemberIds` | GUID array | Yes | One or more CardiMember IDs. **No max-5 cap and no ownership validation** |
| `dateRangeFrom` | date (`DateOnly`) | Yes | Start date. No range-size validation |
| `dateRangeTo` | date (`DateOnly`) | Yes | End date |
| `format` | integer enum | Yes | `ReportFormat`: Pdf=1, Csv=2, FhirR4=3, Hl7V2=4. Accepted but **output is always plain text today** |
| `fhirProfile` | string | No | Default `"us-core"`. Unused today (reserved for FHIR output) |
| `fhirResources` | string array | No | Default `["Patient", "Observation", "Device"]`. Unused today |
| `includeMetrics` | boolean | No | Include daily activity metrics (default `true`) |
| `includeTrends` | boolean | No | Default `true`; currently has no effect on the prompt |
| `includeAlerts` | boolean | No | Include alert history in range (default `true`) |
| `includeNotes` | boolean | No | Default `false`; no notes feature exists |
| `includeDevices` | boolean | No | Default `false`; currently has no effect |
| `title` | string | No | Currently unused |

There is **no validator** registered for this request — malformed values fail model binding (400) but no business rules are enforced.

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

---

## GET `/api/v1/reports/{reportId}`

Check the status of an in-progress or completed report.

**Priority:** P1 | **Auth Required:** Yes

> **Security note (current behavior, tracked as a follow-up):** this endpoint performs **no ownership check** — any authenticated user who knows a report ID can read its status and, via download, its content. The report ID's 128 bits of randomness is the only protection today.

### Response `200 OK` — Ready (wrapped in `ApiResponse<T>`)

```json
{
  "reportId": "8f14e45fceea167a5a36dedd4bea2543",
  "status": 2,
  "progressPercent": null,
  "format": null,
  "contentType": "text/plain",
  "fileSizeBytes": 4210,
  "downloadUrl": "/api/v1/reports/8f14e45fceea167a5a36dedd4bea2543/download",
  "downloadExpiresAt": "2026-08-07T11:00:00Z",
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
- `format` would be the integer `ReportFormat`, but is **currently always `null`** (the requested format is never copied into the stored status).
- `metadata.cardiMembers` contains **GUID strings**, not member names.
- Date-range fields are **flat** (`dateRangeFrom`/`dateRangeTo`), not a nested `dateRange` object.
- On failure, `status` is 3 and `error` is a generic "Report generation failed. Please try again."

**Report Status Values** (integer `ReportStatus` enum):

| Value | Name | Description |
|-------|------|-------------|
| 1 | `Pending` | Generation queued or in progress |
| 2 | `Ready` | Report generated and available for download |
| 3 | `Failed` | Generation failed — see `error` field |
| 4 | `Expired` | **Never assigned in practice** — expiry manifests as cache eviction (404), not this status |

### Errors

| Status | When |
|--------|------|
| 404 | Report ID unknown **or past the 1-hour TTL** ("it may have expired") |

---

## GET `/api/v1/reports/{reportId}/download`

Download the generated report. **The download window is 1 hour** from generation (same TTL as status) — not 24 hours.

**Priority:** P1 | **Auth Required:** Yes (same missing-ownership caveat as the status endpoint)

### Response `200 OK`

Always a plain-text file, regardless of the requested `format`:

```
Content-Type: text/plain; charset=utf-8
Content-Disposition: attachment; filename="report-8f14e45fceea167a5a36dedd4bea2543.txt"
```

The body is the LLM-generated report text (structured prose summarising metrics, alerts, and trends for a non-clinical caregiver).

> **Planned — not yet implemented:** PDF/CSV/FHIR R4/HL7 v2 rendering, per-format content types and filenames, the HIPAA footer, `X-HIPAA-Confidential` headers, and FHIR `meta.security` labels. None of that exists today.

### Errors

| Status | When |
|--------|------|
| 404 | Report unknown or expired (1-hour TTL), or content evicted — **410 is never returned** |
| 409 | Report exists but is not `Ready` yet (still pending, or failed) |

---

**Related:** [readme.md](readme.md) | [health-data.md](health-data.md) | [User Stories 2.3, 9.2](../../ui/mobile/user_stories.md)

**Last Updated:** August 7, 2026
