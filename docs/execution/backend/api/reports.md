# Reports API

Handles async generation and download of health summary reports for doctor visits. Report generation is asynchronous due to rendering/packaging time. Supports PDF, CSV, and FHIR R4 formats (MVP 1); HL7 v2 added in MVP 2.

**User Stories:** 2.3 (Trend Charts & Historical Data — export), 6.3 (Health Data Export), 9.2 (Printable Reports)

---

## POST `/api/v1/reports/generate`

Initiate async generation of a health summary report for one or more CardiMembers. Returns a report ID to poll for completion. FHIR R4 and HL7 v2 formats follow the same async pattern as PDF/CSV.

**Priority:** P0 (PDF, CSV, FHIR R4 — MVP 1) / P1 (HL7 v2 — MVP 2) | **Auth Required:** Yes

### Request Body

```json
{
  "cardiMemberIds": ["cm_01J8K2..."],
  "dateRange": {
    "from": "2026-02-07",
    "to": "2026-03-09"
  },
  "format": "fhir_r4",
  "fhirProfile": "us-core",
  "fhirResources": ["Patient", "Observation", "Device"],
  "sections": {
    "metrics": true,
    "trends": true,
    "alerts": true,
    "notes": true,
    "devices": false
  },
  "title": "Health Summary for Dr. Smith Visit"
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `cardiMemberIds` | array | Yes | One or more CardiMember IDs (max 5) |
| `dateRange.from` | string (ISO 8601) | Yes | Start date |
| `dateRange.to` | string (ISO 8601) | Yes | End date (max 365 days) |
| `format` | string | Yes | `"pdf"`, `"csv"`, `"fhir_r4"` **(MVP 1)**, or `"hl7_v2"` **(MVP 2)** |
| `fhirProfile` | string | No | FHIR profile to apply — `"us-core"` (default) or `"international"`. Only used when `format` is `fhir_r4` |
| `fhirResources` | array | No | FHIR resource types to include — default: `["Patient", "Observation", "Device"]`. Only used when `format` is `fhir_r4` |
| `sections.metrics` | boolean | No | Include key metric summary table (default: true). Applies to PDF/CSV only |
| `sections.trends` | boolean | No | Include trend charts (default: true). Applies to PDF/CSV only |
| `sections.alerts` | boolean | No | Include alert history (default: true). Applies to PDF/CSV only |
| `sections.notes` | boolean | No | Include shared care notes (default: false). Applies to PDF/CSV only |
| `sections.devices` | boolean | No | Include device source information (default: false). Applies to PDF/CSV only |
| `title` | string | No | Custom report title (max 100 chars). Applies to PDF/CSV only |

> **Format availability by MVP:**
> | Format | MVP | Content-Type |
> |--------|-----|--------------|
> | `pdf` | MVP 1 | `application/pdf` |
> | `csv` | MVP 1 | `text/csv` |
> | `fhir_r4` | MVP 1 | `application/fhir+json` |
> | `hl7_v2` | MVP 2 | `application/hl7-v2+er7` |

### Response `202 Accepted`

```json
{
  "reportId": "rpt_abc123",
  "status": "pending",
  "estimatedReadyInSeconds": 10,
  "statusUrl": "/api/v1/reports/rpt_abc123"
}
```

### Errors

| Code | Status | Description |
|------|--------|-------------|
| `INVALID_CARDIMEMBER` | 400 | One or more CardiMember IDs are invalid or inaccessible |
| `DATE_RANGE_TOO_LARGE` | 400 | Date range exceeds 365 days |
| `NO_DATA_IN_RANGE` | 422 | No health data found in the specified date range |
| `INVALID_FHIR_PROFILE` | 400 | Unknown FHIR profile specified — use `us-core` or `international` |
| `FORMAT_NOT_AVAILABLE` | 403 | Requested format is not available on this plan or MVP release |
| `EXPORT_NOT_AVAILABLE_ON_PLAN` | 403 | Export requires Complete Care plan |

---

## GET `/api/v1/reports/{reportId}`

Check the status of an in-progress or completed report.

**Priority:** P1 | **Auth Required:** Yes

### Path Parameters

| Parameter | Description |
|-----------|-------------|
| `reportId` | Report ID returned from the generate endpoint |

### Response `200 OK` — Pending

```json
{
  "reportId": "rpt_abc123",
  "status": "pending",
  "progressPercent": 40,
  "createdAt": "2026-03-09T10:00:00Z"
}
```

### Response `200 OK` — Ready

```json
{
  "reportId": "rpt_abc123",
  "status": "ready",
  "format": "fhir_r4",
  "contentType": "application/fhir+json",
  "fileSizeBytes": 312400,
  "downloadUrl": "/api/v1/reports/rpt_abc123/download",
  "downloadExpiresAt": "2026-03-10T10:00:00Z",
  "createdAt": "2026-03-09T10:00:00Z",
  "completedAt": "2026-03-09T10:00:08Z",
  "metadata": {
    "cardiMembers": ["Margaret Doe"],
    "dateRange": { "from": "2026-02-07", "to": "2026-03-09" },
    "fhirProfile": "us-core",
    "fhirResources": ["Patient", "Observation", "Device"]
  }
}
```

### Response `200 OK` — Failed

```json
{
  "reportId": "rpt_abc123",
  "status": "failed",
  "error": "Insufficient data in the selected date range to generate a meaningful report.",
  "createdAt": "2026-03-09T10:00:00Z"
}
```

**Report Status Values:**

| Status | Description |
|--------|-------------|
| `pending` | Generation queued or in progress |
| `ready` | Report generated and available for download |
| `failed` | Generation failed — see `error` field |
| `expired` | Download link expired (24-hour TTL) |

### Errors

| Code | Status | Description |
|------|--------|-------------|
| `REPORT_NOT_FOUND` | 404 | Report ID not found or does not belong to user |

---

## GET `/api/v1/reports/{reportId}/download`

Download the generated report file. Link expires 24 hours after report completion.

**Priority:** P1 | **Auth Required:** Yes

### Path Parameters

| Parameter | Description |
|-----------|-------------|
| `reportId` | Report ID |

### Response `200 OK`

Content-Type varies by report format:

| Format | Content-Type | Content-Disposition filename |
|--------|-------------|------------------------------|
| `pdf` | `application/pdf` | `carditrack-[member]-[date].pdf` |
| `csv` | `text/csv` | `carditrack-[member]-[date].csv` |
| `fhir_r4` | `application/fhir+json` | `carditrack-[member]-[date]-fhir.json` |
| `hl7_v2` | `application/hl7-v2+er7` | `carditrack-[member]-[date]-hl7.hl7` |

```
Content-Type: application/fhir+json
Content-Disposition: attachment; filename="carditrack-margaret-doe-2026-03-09-fhir.json"
X-HIPAA-Confidential: true
```

Binary/text file stream depending on format.

> PDF reports include a HIPAA-compliant footer on every page:
> `"Confidential Health Information — Generated by CardiTrack on [date]. Authorized access only."`
>
> FHIR R4 bundles include `meta.security` with `http://terminology.hl7.org/CodeSystem/v3-Confidentiality|R` (Restricted).
>
> HL7 v2 messages include MSH segment with appropriate security/confidentiality fields per HL7 v2.5.1.

### Errors

| Code | Status | Description |
|------|--------|-------------|
| `REPORT_NOT_READY` | 409 | Report generation not yet complete |
| `REPORT_EXPIRED` | 410 | Download link has expired — regenerate the report |
| `REPORT_NOT_FOUND` | 404 | Report ID not found |

---

**Related:** [readme.md](readme.md) | [health-data.md](health-data.md) | [User Stories 2.3, 9.2](../../ui/mobile/user_stories.md)
