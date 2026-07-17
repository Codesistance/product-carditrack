# Health Data API

Provides health metrics, daily summaries, trend charts, baselines, and multi-member dashboard data for caregivers. Also supports offline caching via timestamped data and data export.

**User Stories:** 2.1 (Daily Health Overview), 2.2 (Multi-Member Dashboard), 2.3 (Trend Charts), 5.2 (Mobile Widget), 6.3 (Health Data Export), 10.1 (Offline Support)

---

## GET `/api/v1/dashboard`

Returns a summary for all CardiMembers accessible to the authenticated user. Used as the main dashboard view and mobile widget data source.

**Priority:** P0 | **Auth Required:** Yes

### Query Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `sort` | string | `"status"` (alerts first, default) or `"name"` |
| `filter` | string | `"alerts"` — show only members with active alerts |

### Response `200 OK`

```json
{
  "generatedAt": "2026-03-09T10:00:00Z",
  "cardimembers": [
    {
      "id": "cm_01J8K2...",
      "name": "Margaret Doe",
      "photoUrl": "https://cdn.carditrack.com/photos/cm_01J8K2.jpg",
      "healthStatus": "yellow",
      "activeAlertCount": 1,
      "lastSyncedAt": "2026-03-09T08:30:00Z",
      "monitoringPaused": false,
      "summary": {
        "steps": {
          "value": 2500,
          "baseline": 5000,
          "changePercent": -50,
          "unit": "steps/day"
        },
        "restingHeartRate": {
          "value": 68,
          "baseline": 65,
          "changePercent": 4.6,
          "unit": "bpm"
        },
        "sleepHours": {
          "value": 7.2,
          "baseline": 7.5,
          "changePercent": -4,
          "unit": "hours"
        }
      }
    }
  ],
  "total": 1
}
```

**Health Status Values:**

| Value | Meaning |
|-------|---------|
| `green` | All metrics within normal range |
| `yellow` | Minor deviation — worth monitoring |
| `orange` | Notable deviation — recommend action |
| `red` | Critical alert — immediate attention |
| `unknown` | Insufficient data (baseline still learning) |

---

## GET `/api/v1/cardimembers/{id}/health/summary`

Get the daily health overview for a single CardiMember, including all key metrics and comparison to baseline.

**Priority:** P0 | **Auth Required:** Yes

### Path Parameters

| Parameter | Description |
|-----------|-------------|
| `id` | CardiMember ID |

### Query Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `date` | string (ISO 8601) | Specific date (default: today) |

### Response `200 OK`

```json
{
  "cardiMemberId": "cm_01J8K2...",
  "date": "2026-03-09",
  "healthStatus": "yellow",
  "lastSyncedAt": "2026-03-09T08:30:00Z",
  "metrics": {
    "steps": {
      "value": 2500,
      "baseline": 5000,
      "changePercent": -50,
      "unit": "steps",
      "status": "yellow"
    },
    "restingHeartRate": {
      "value": 68,
      "baseline": 65,
      "changePercent": 4.6,
      "unit": "bpm",
      "status": "green"
    },
    "sleepHours": {
      "value": 7.2,
      "baseline": 7.5,
      "changePercent": -4,
      "unit": "hours",
      "status": "green"
    },
    "activeMinutes": {
      "value": 18,
      "baseline": 45,
      "changePercent": -60,
      "unit": "minutes",
      "status": "yellow"
    }
  },
  "deviceSource": {
    "deviceId": "dev_01J9...",
    "provider": "fitbit",
    "displayName": "Fitbit Charge 6"
  }
}
```

### Errors

| Code | Status | Description |
|------|--------|-------------|
| `NO_DEVICE_CONNECTED` | 422 | No active devices for this CardiMember |
| `NO_DATA_FOR_DATE` | 404 | No health data available for the requested date |

---

## GET `/api/v1/cardimembers/{id}/health/trends`

Get time-series trend data for charts. Supports predefined and custom date ranges.

**Priority:** P1 | **Auth Required:** Yes

### Query Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `range` | string | Yes* | `7d`, `30d`, `90d`, or `custom` |
| `from` | string (ISO 8601) | If `range=custom` | Start date |
| `to` | string (ISO 8601) | If `range=custom` | End date (max 365 days) |
| `metrics` | string | No | Comma-separated: `steps,heartRate,sleep,activeMinutes` (default: all) |

### Response `200 OK`

```json
{
  "cardiMemberId": "cm_01J8K2...",
  "range": "30d",
  "from": "2026-02-07",
  "to": "2026-03-09",
  "baseline": {
    "steps": 5000,
    "restingHeartRate": 65,
    "sleepHours": 7.5,
    "activeMinutes": 45
  },
  "series": [
    {
      "date": "2026-02-07",
      "steps": 4800,
      "restingHeartRate": 66,
      "sleepHours": 7.3,
      "activeMinutes": 40,
      "alerts": []
    },
    {
      "date": "2026-03-09",
      "steps": 2500,
      "restingHeartRate": 68,
      "sleepHours": 7.2,
      "activeMinutes": 18,
      "alerts": ["alert_xyz_001"]
    }
  ]
}
```

> `alerts` array in each data point contains alert IDs that occurred on that date — enables chart annotations.

---

## GET `/api/v1/cardimembers/{id}/health/baseline`

Get the current calculated baseline values and learning progress for a CardiMember.

**Priority:** P1 | **Auth Required:** Yes

### Response `200 OK`

```json
{
  "cardiMemberId": "cm_01J8K2...",
  "learningProgress": {
    "daysCaptured": 12,
    "daysRequired": 30,
    "percentComplete": 40,
    "estimatedReadyDate": "2026-04-01"
  },
  "baselineValues": {
    "steps": 5000,
    "restingHeartRate": 65,
    "sleepHours": 7.5,
    "activeMinutes": 45,
    "typicalWakeTime": "07:00",
    "typicalSleepTime": "22:30"
  },
  "usingStatisticalAlerts": true,
  "baselineLastUpdatedAt": "2026-03-08T23:00:00Z"
}
```

---

## POST `/api/v1/cardimembers/{id}/health-data/batch`

Device-bridge ingestion for **on-device providers** (Apple Health). The CardiTrack mobile app reads HealthKit locally and uploads normalized daily samples. Not used by server-OAuth providers, whose data arrives via webhooks.

**Priority:** P1 (ships with Apple Watch support — see [release matrix](../../../release_matrix.md)) | **Auth Required:** Yes

### Request Body

```json
{
  "deviceId": "dev_03J9...",
  "samples": [
    {
      "date": "2026-03-09",
      "steps": 4100,
      "restingHeartRate": 66,
      "sleepMinutes": 432,
      "activeMinutes": 38
    }
  ]
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `deviceId` | string | Yes | The on-device-bridge connection this data belongs to |
| `samples` | array | Yes | Normalized daily samples (max 90 per request); upserted by `(cardiMemberId, date, dataSource)` |

### Response `202 Accepted`

```json
{
  "accepted": 1,
  "rejected": 0
}
```

### Errors

| Code | Status | Description |
|------|--------|-------------|
| `DEVICE_NOT_FOUND` | 404 | Device ID not found for this CardiMember |
| `INVALID_DEVICE_MODE` | 422 | Device is a server-OAuth connection; batch upload not permitted |

---

## GET `/api/v1/cardimembers/{id}/health/export`

Export health data for a CardiMember. Supports human-readable formats (PDF, CSV) and interoperable medical formats (FHIR R4, HL7 v2). Used for doctor visit preparation and EHR integration.

**Priority:** P0 (PDF, CSV, FHIR R4 in MVP 1) | P1 (HL7 v2 added in MVP 2) | **Auth Required:** Yes

### Query Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `format` | string | Yes | `pdf`, `csv`, `fhir_r4` (**MVP 1**), or `hl7_v2` (MVP 2) |
| `range` | string | Yes | `7d`, `30d`, `90d`, or `custom` |
| `from` | string (ISO 8601) | If `range=custom` | Start date |
| `to` | string (ISO 8601) | If `range=custom` | End date |
| `sections` | string | No | Comma-separated: `metrics,alerts,notes` (default: all). Ignored for `fhir_r4` and `hl7_v2` — full dataset always exported in medical formats. |
| `fhir_profile` | string | No | FHIR R4 profile to validate against. Default: `us-core`. Only valid when `format=fhir_r4`. |
| `fhir_resources` | string | No | Comma-separated FHIR resource types to include. Default: `Patient,Observation,Device`. Only valid when `format=fhir_r4`. |

### Response `200 OK`

**PDF / CSV:**
```
Content-Type: application/pdf  (or text/csv)
Content-Disposition: attachment; filename="carditrack-margaret-doe-2026-03-09.pdf"
```

**FHIR R4:**
```
Content-Type: application/fhir+json
Content-Disposition: attachment; filename="carditrack-margaret-doe-fhir-r4-2026-03-09.json"
```

Returns a FHIR R4 Bundle (type: `document`) containing:
- `Patient` — CardiMember demographics
- `Observation` — activity, heart rate, and sleep measurements (LOINC-coded)
- `Device` — connected wearable device(s)
- `Condition` — any logged conditions from medical notes (if consented)

**HL7 v2 (MVP 2):**
```
Content-Type: application/hl7-v2+er7
Content-Disposition: attachment; filename="carditrack-margaret-doe-hl7-v2-2026-03-09.hl7"
```

Returns an HL7 v2.5.1 ORU^R01 message containing observation results.

All responses: binary file stream.

### Errors

| Code | Status | Description |
|------|--------|-------------|
| `EXPORT_RANGE_TOO_LARGE` | 400 | Range exceeds 365 days |
| `NO_DATA_IN_RANGE` | 404 | No health data in the specified range |
| `FORMAT_NOT_AVAILABLE` | 403 | Requested format not available in current MVP (e.g., `hl7_v2` before MVP 2 release) |
| `INVALID_FHIR_PROFILE` | 400 | Specified `fhir_profile` is not supported |

---

**Related:** [readme.md](readme.md) | [alerts.md](alerts.md) | [reports.md](reports.md) | [User Stories 2.1, 2.2, 2.3, 5.2, 10.1](../../ui/mobile/user_stories.md)
