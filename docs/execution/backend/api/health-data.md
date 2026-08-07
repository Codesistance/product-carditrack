# Health Data API

Provides health metrics, baselines, and dashboard data for caregivers.

**Implementation status:** the **per-member dashboard** below is implemented and is the single health-data read endpoint today (plus the AI narrative baseline under `/api/v1/insights`). The multi-member dashboard, daily summary, trends, numeric baseline, batch ingestion, and export endpoints are **planned — not yet implemented** and marked as such.

**User Stories:** 2.1 (Daily Health Overview), 2.2 (Multi-Member Dashboard), 2.3 (Trend Charts), 5.2 (Mobile Widget), 6.3 (Health Data Export), 10.1 (Offline Support)

---

## GET `/api/v1/cardimembers/{id}/dashboard` — implemented

Composed dashboard payload for **one CardiMember** (mobile Main Dashboard, M1-09): hero status, key metrics with 7-day series, recent alerts, and device/baseline state in a single round-trip. (There is **no** account-wide `GET /api/v1/dashboard` — the multi-member view is assembled client-side from `GET /api/Onboarding/cardimembers` plus per-member calls.)

**Priority:** P0 | **Auth Required:** Yes (active `UserCardiMember` link with `CanViewHealthData`)

### Path Parameters

| Parameter | Description |
|-----------|-------------|
| `id` | CardiMember ID (GUID) |

### Response `200 OK`

Wrapped in the standard `ApiResponse<T>` envelope:

```json
{
  "cardiMemberId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "name": "Margaret Doe",
  "age": 80,
  "phone": "+15551234567",
  "photoUrl": null,
  "healthStatus": "yellow",
  "lastSyncedAt": "2026-08-07T08:30:00Z",
  "unreadAlertCount": 1,
  "device": {
    "hasActiveConnection": true,
    "deviceType": "Fitbit",
    "deviceName": "Fitbit Charge 6",
    "connectionStatus": "Connected",
    "lastSyncDate": "2026-08-07T08:30:00Z"
  },
  "baseline": {
    "isLearning": false,
    "daysCaptured": 30,
    "daysRequired": 30,
    "percentComplete": 100
  },
  "metrics": {
    "steps": {
      "value": 2500,
      "baseline": 5000,
      "changePercent": -50.0,
      "unit": "steps",
      "status": "yellow",
      "goal": 5000,
      "rangeLow": null,
      "rangeHigh": null,
      "qualityScore": null,
      "series": [
        { "date": "2026-08-01", "value": 4800 },
        { "date": "2026-08-07", "value": 2500 }
      ]
    },
    "restingHeartRate": {
      "value": 68,
      "baseline": 65,
      "changePercent": 4.6,
      "unit": "bpm",
      "status": "green",
      "goal": null,
      "rangeLow": 61,
      "rangeHigh": 69,
      "qualityScore": null,
      "series": [ { "date": "2026-08-07", "value": 68 } ]
    },
    "sleep": {
      "value": 7.2,
      "baseline": 7.5,
      "changePercent": -4.0,
      "unit": "hours",
      "status": "green",
      "goal": null,
      "rangeLow": null,
      "rangeHigh": null,
      "qualityScore": 4,
      "series": [ { "date": "2026-08-07", "value": 7.2 } ]
    }
  },
  "recentAlerts": [
    {
      "alertId": "9b2f5f64-5717-4562-b3fc-2c963f66afa6",
      "type": "Inactivity",
      "severity": "yellow",
      "title": "Margaret's activity is lower than usual",
      "message": "Steps well below baseline for two days.",
      "triggeredAt": "2026-08-07T09:00:00Z",
      "isAcknowledged": false
    }
  ],
  "generatedAt": "2026-08-07T10:00:00Z"
}
```

Field notes:

- The sleep metric key is **`sleep`** (not `sleepHours`); there is **no `activeMinutes` metric**.
- `photoUrl` is always `null` today (no photo storage exists).
- `metrics` is **`null`** when the member has no activity logs in the last 30 days.
- Each metric carries a 7-day `series` of `{date, value}` points (missing days → `value: null`).
- `device.connectionStatus` is the internal enum name (`Connected`, `TokenExpired`, …) — unlike the lowercase statuses in [devices.md](devices.md).
- `recentAlerts` holds the **5 most recent** active alerts; `unreadAlertCount` counts unresolved, unacknowledged alerts.
- `goal` on steps defaults to the baseline average (or 10 000 when no baseline); `rangeLow`/`rangeHigh` are heart-rate mean ± one standard deviation; `qualityScore` is a 1–5 sleep-efficiency bucket.

**Health Status Values** (`healthStatus` and per-metric `status` — lowercase strings):

| Value | Meaning |
|-------|---------|
| `green` | All metrics within normal range |
| `yellow` | Minor deviation — worth monitoring |
| `orange` | Notable deviation — recommend action |
| `red` | Critical alert — immediate attention |
| `unknown` | Insufficient data (baseline still learning) |

**Deviation thresholds and baseline window** (fixed constants, no per-user sensitivity settings):

- Baselines are computed over a **30-day window** (`daysRequired: 30`); `isLearning` is true until a pattern baseline exists.
- Per-metric status: deviation from baseline **≤ 30%** → `green`, **> 30%** → `yellow`, **> 50%** → `orange`. (`red` comes only from alert severity, not metric deviation.)
- The member-level `healthStatus` is the worst unresolved alert severity, else `green` (or `unknown` while learning / no data).

### Errors

| Status | When |
|--------|------|
| 403 | JWT valid but no local user row |
| 404 | No active, view-permitted link between the caller and this CardiMember, or member inactive |

---

## GET `/api/v1/insights/members/{id}/baseline` — implemented (AI narrative)

MedGemma-generated **narrative** analysis of a CardiMember's baseline trends — this is prose, not the numeric baseline endpoint planned below.

**Priority:** P1 | **Auth Required:** Yes

### Response `200 OK` (wrapped in `ApiResponse<T>`)

```json
{
  "cardiMemberId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "summary": "Margaret's activity has been stable over the past month...",
  "keyFindings": [
    "Resting heart rate trending slightly upward",
    "Sleep duration consistent with baseline"
  ],
  "isLearning": false,
  "generatedAt": "2026-08-07T10:00:00Z"
}
```

**`isLearning`** is `true` until the member has a 30-day `PatternBaseline` — the same test the dashboard's `baseline.isLearning` makes. While it is true, the summary describes **what has been observed so far**, not how the member compares to normal, because no normal has been established yet:

```json
{
  "summary": "So far the readings show a settled daily rhythm: activity concentrated in the late morning, and sleep starting at a consistent hour...",
  "keyFindings": ["Nine days of data captured so far"],
  "isLearning": true
}
```

Clients must not label that output as a trend assessment. The prompt behind it is forbidden from calling anything elevated, low, or a deviation.

The prompt carries a member context block — age, sex, and caregiver-entered medical notes — because a resting heart rate is not interpretable without them. The member's **name and id are never sent** to the model.

---

## GET `/api/v1/cardimembers/{id}/health/summary`

> **Planned — not yet implemented.** The dashboard endpoint above is the current source for daily metrics.

Get the daily health overview for a single CardiMember for a specific date, including all key metrics and comparison to baseline. (Design intent: adds a `date` query parameter, an `activeMinutes` metric, and a `deviceSource` block.)

**Priority:** P0

---

## GET `/api/v1/cardimembers/{id}/health/trends`

> **Planned — not yet implemented.** Today the only time-series available is the 7-day `series` inside each dashboard metric.

Get time-series trend data for charts over `7d`/`30d`/`90d`/custom ranges, with per-day alert annotations.

**Priority:** P1

---

## GET `/api/v1/cardimembers/{id}/health/baseline`

> **Planned — not yet implemented.** The dashboard's `baseline` block covers learning progress today; the AI narrative version exists at `GET /api/v1/insights/members/{id}/baseline` (above) but returns prose, not numbers.

Get the current calculated numeric baseline values (steps, resting heart rate, sleep, typical wake/sleep times) and learning progress.

**Priority:** P1

---

## POST `/api/v1/cardimembers/{id}/health-data/batch`

> **Planned — not yet implemented.** No Apple Health bridge exists — there is no ingestion endpoint, and server-OAuth data arrives via the Worker's 30-minute provider poll (not webhooks).

Device-bridge ingestion for **on-device providers** (Apple Health): the mobile app would read HealthKit locally and upload normalized daily samples.

**Priority:** P1 (ships with Apple Watch support — see [release matrix](../../../release_matrix.md))

---

## GET `/api/v1/cardimembers/{id}/health/export`

> **Planned — not yet implemented.** No FHIR/HL7/PDF/CSV export code exists (only unused DTO fields on the reports request). The nearest current capability is the LLM-generated text report — see [reports.md](reports.md).

Export health data in human-readable (PDF, CSV) and interoperable medical formats (FHIR R4, HL7 v2) for doctor visits and EHR integration.

**Priority:** P0 (PDF, CSV, FHIR R4 in MVP 1) | P1 (HL7 v2 added in MVP 2)

---

**Related:** [readme.md](readme.md) | [alerts.md](alerts.md) | [reports.md](reports.md) | [devices.md](devices.md) | [User Stories 2.1, 2.2, 2.3, 5.2, 10.1](../../ui/mobile/user_stories.md)

**Last Updated:** August 7, 2026
