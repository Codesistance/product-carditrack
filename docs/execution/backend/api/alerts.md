# Alerts API

> **Status: Partially implemented.** The P0 list/acknowledge slice backing the mobile Alerts List (M1-10) ships in `AlertsController`; everything else below is still design intent. See "Implemented today" for exactly what exists.

Handles alert retrieval, acknowledgment, status lifecycle, photo attachments, and per-member alert notification preferences including quiet hours and sensitivity.

**User Stories:** 3.1 (Receiving Critical Alerts), 3.2 (Managing Alert Notifications), 3.3 (Alert Acknowledgment & Notes), 11.1 (Activity Alerts), 11.2 (Heart Rate Alerts), 11.3 (Pattern Break Alerts)

---

## Implemented today

### GET `/api/v1/insights/alerts/{alertId}` — AI alert analysis

The one alert-related endpoint that exists: on-demand **MedGemma analysis** of a single alert (explanation, severity, recommended action). Returns 200 with `ApiResponse<AlertInsightResponse>`, **404** for an unknown alert ID.

```json
{
  "alertId": "9b2f5f64-5717-4562-b3fc-2c963f66afa6",
  "explanation": "Margaret's step count dropped 50% below her 30-day baseline...",
  "severity": 2,
  "recommendedAction": "Consider a check-in call today."
}
```

> `severity` is the **integer** `AlertSeverity` enum (Green=1, Yellow=2, Orange=3, Red=4) — enums serialize as integers on the wire (see [readme.md](readme.md)).

### The M1-10 slice — `AlertsController`

Three endpoints are live, serving the mobile Alerts List:

| Endpoint | Notes |
|----------|-------|
| `GET /api/v1/alerts` | Query params `cardiMemberId`, `severity`, `status`, `from`, `to`, `limit` (default 50, max 200), `offset`. Scoped to the members the caller may read via `ICardiMemberAccessService`; an unreadable `cardiMemberId` returns **404**, not 403, for the usual non-disclosure reason. Unrecognised `severity`/`status` values are rejected with **400** rather than silently ignored. |
| `GET /api/v1/cardimembers/{id}/alerts` | Same filters, single member. |
| `POST /api/v1/alerts/{alertId}/acknowledge` | No request body. Idempotent — re-acknowledging keeps the original timestamp and acknowledger, so a second family member tapping "handled" doesn't overwrite who dealt with it. |

Response shape differs from the design below in three ways, all because the implemented `Alert` entity is what it is:

- `type` is the **`AlertType` display name** ("Inactivity", "Heart Rate", "Sleep", "Pattern Break", "Trend"), not the `activity_decline` string taxonomy.
- `severity` is the lowercase `AlertSeverity` name (`green`/`yellow`/`orange`/`red`), and `status` is derived from `AcknowledgedDate` + `IsResolved` rather than stored — see `AlertStatus`.
- Each summary carries `cardiMemberName`, `emergencyContactPhone` and `emergencyContactName` so the M1-10 card can render its avatar and Call action without a second round-trip. `cardiMemberPhotoUrl` is present but always null: no member photo storage exists yet.

**Still not implemented:** alert detail, status transitions (`PUT .../status`), notes, photos, history, and alert preferences. Acknowledgment takes no `note`/`actionTaken` — notes belong to the unbuilt M1-11/M1-12 detail screens and would need a schema change.

Alert **summaries** also surface in the dashboard's `recentAlerts` array — see [health-data.md](health-data.md).

### Actual alert-type taxonomy

The implemented `AlertType` enum (integers on the wire) differs from the string taxonomy designed below:

| Value | Name | Meaning |
|-------|------|---------|
| 1 | `Inactivity` | Activity well below baseline |
| 2 | `HeartRate` | Resting HR outside normal range |
| 3 | `Sleep` | Sleep duration significantly off baseline |
| 4 | `PatternBreak` | Break from established daily pattern |
| 5 | `Trend` | Multi-week decline trend |

### Sensitivity: fixed constants only

There are **no sensitivity settings, quiet hours, or channel routing** in the system. The only thresholds that exist are the fixed dashboard-coloring constants (deviation > 30% → yellow, > 50% → orange — the "medium" profile below, hard-coded).

The nearest artifact is an **unwired** `NotificationPreferencesRequest` DTO (validator registered, no endpoint consumes it), whose intended shape is simpler than the model designed below:

```json
{
  "cardiMemberId": "3fa85f64-...",
  "receiveSmsAlerts": false,
  "receiveEmailAlerts": true,
  "receivePushAlerts": true,
  "enabledAlertTypes": [1, 2, 4],
  "quietHoursStart": "22:00",
  "quietHoursEnd": "07:00"
}
```

(`enabledAlertTypes` are `AlertType` integers; quiet hours are plain `TimeOnly` values with no timezone or severity override.)

Everything below is the **planned** contract, kept as design intent.

---

## GET `/api/v1/alerts`

> **Implemented** — see "The M1-10 slice" above for how the live response differs from the design intent below.

List all alerts across all accessible CardiMembers.

**Priority:** P0 | **Auth Required:** Yes

### Query Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `cardiMemberId` | string | Filter by specific CardiMember |
| `severity` | string | `yellow`, `orange`, `red` |
| `status` | string | `new`, `acknowledged`, `resolved` |
| `from` | string (ISO 8601) | Start date filter |
| `to` | string (ISO 8601) | End date filter |
| `limit` | integer | Max results (default: 50, max: 200) |
| `offset` | integer | Pagination offset |

### Response `200 OK`

```json
{
  "alerts": [
    {
      "alertId": "alert_xyz_001",
      "cardiMemberId": "cm_01J8K2...",
      "cardiMemberName": "Margaret Doe",
      "type": "activity_decline",
      "severity": "yellow",
      "status": "new",
      "headline": "Margaret's activity is lower than usual",
      "description": "Margaret's steps: 2,500/day. Normal: 5,000/day (-50%). This could indicate illness, pain, or low mood.",
      "triggeredAt": "2026-03-09T09:00:00Z",
      "acknowledgedAt": null,
      "acknowledgedBy": null
    }
  ],
  "total": 1,
  "unreadCount": 1
}
```

**Alert Types** (design-intent scenarios; the implemented enum is the five-value integer `AlertType` in "Implemented today" above — mappings shown):

| Type | Severity Range | Implemented enum | Description |
|------|---------------|------------------|-------------|
| `activity_decline` | yellow | `Inactivity` (1) | Gradual step/activity reduction |
| `elevated_heart_rate` | orange | `HeartRate` (2) | Resting HR above normal range |
| `no_morning_activity` | red | `PatternBreak` (4) | No movement detected past typical wake time (device is syncing) |
| `irregular_sleep` | yellow | `Sleep` (3) | Sleep duration significantly off baseline |
| `device_disconnected` | yellow | — (no equivalent) | Wearable not syncing (>2h silence during waking hours) |
| `long_term_trend` | orange | `Trend` (5) | Multi-week decline trend (e.g. steps −5%/week for 4 weeks) — *ships with the AI pipeline, see [release matrix](../../../release_matrix.md)* |

> Severities use the product taxonomy (`yellow`/`orange`/`red`). `green` is a *health status*, not an alert severity — no alert is emitted for normal states. The AI pipeline's internal Critical/High/Medium/Low scale maps to these values — see [llm_design.md](../../../llm_design.md).

---

## GET `/api/v1/cardimembers/{id}/alerts`

> **Implemented.**

List alerts for a specific CardiMember.

**Priority:** P0 | **Auth Required:** Yes

Supports the same query parameters as `GET /api/v1/alerts` (except `cardiMemberId`).

### Response `200 OK`

Same schema as `GET /api/v1/alerts`.

---

## GET `/api/v1/alerts/{alertId}`

Get full detail for a single alert, including context, recommended actions, and alert history frequency.

**Priority:** P0 | **Auth Required:** Yes

### Path Parameters

| Parameter | Description |
|-----------|-------------|
| `alertId` | Alert ID |

### Response `200 OK`

```json
{
  "alertId": "alert_xyz_001",
  "cardiMemberId": "cm_01J8K2...",
  "cardiMemberName": "Margaret Doe",
  "type": "no_morning_activity",
  "severity": "red",
  "status": "new",
  "headline": "Margaret hasn't moved today",
  "description": "Margaret hasn't moved today. Typical wake time: 7:00am. Current time: 11:00am.",
  "context": {
    "lastActivityAt": "2026-03-08T22:45:00Z",
    "typicalWakeTime": "07:00",
    "currentTime": "11:00",
    "frequencyNote": "This is the first time this month."
  },
  "recommendedActions": [
    {
      "id": "call",
      "label": "Call now",
      "actionType": "phone_call",
      "isPrimary": true
    },
    {
      "id": "check_in_person",
      "label": "I'm checking in person",
      "actionType": "acknowledge_with_note",
      "isPrimary": false
    },
    {
      "id": "dismiss_with_note",
      "label": "He told me he'd sleep in today",
      "actionType": "acknowledge_with_note",
      "isPrimary": false
    }
  ],
  "triggeredAt": "2026-03-09T09:00:00Z",
  "acknowledgedAt": null,
  "acknowledgedBy": null,
  "notes": [],
  "photos": []
}
```

### Errors

| Code | Status | Description |
|------|--------|-------------|
| `ALERT_NOT_FOUND` | 404 | Alert ID not found or not accessible |

---

## POST `/api/v1/alerts/{alertId}/acknowledge`

> **Partially implemented** — acknowledgment works and is idempotent; the optional note, `actionTaken`, and the family notification are not built.

Acknowledge an alert with an optional note. Notifies all other family members that the alert has been handled.

**Priority:** P0 | **Auth Required:** Yes

### Request Body

```json
{
  "note": "Called, she had a cold but is fine.",
  "actionTaken": "call"
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `note` | string | No | Free-text note about action taken |
| `actionTaken` | string | No | ID from `recommendedActions` (for analytics) |

### Response `200 OK`

```json
{
  "alertId": "alert_xyz_001",
  "status": "acknowledged",
  "acknowledgedAt": "2026-03-09T11:15:00Z",
  "acknowledgedBy": {
    "userId": "usr_01J8K2...",
    "name": "Jane Doe"
  },
  "note": "Called, she had a cold but is fine.",
  "familyNotified": true
}
```

---

## PUT `/api/v1/alerts/{alertId}/status`

Update alert status. Follows the lifecycle: `new` → `acknowledged` → `resolved`.

**Priority:** P1 | **Auth Required:** Yes

### Request Body

```json
{
  "status": "resolved",
  "note": "Doctor confirmed — minor infection, now recovering."
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `status` | string | Yes | `acknowledged` or `resolved` |
| `note` | string | No | Resolution note |

### Response `200 OK`

```json
{
  "alertId": "alert_xyz_001",
  "status": "resolved",
  "resolvedAt": "2026-03-10T14:00:00Z"
}
```

### Errors

| Code | Status | Description |
|------|--------|-------------|
| `INVALID_STATUS_TRANSITION` | 422 | Cannot transition from current status to requested status |

---

## POST `/api/v1/alerts/{alertId}/photos`

Attach a photo to an alert (e.g. a photo from a doctor visit).

**Priority:** P2 | **Auth Required:** Yes

### Request Body (`multipart/form-data`)

| Field | Type | Description |
|-------|------|-------------|
| `photo` | file | JPEG/PNG, max 10MB |
| `caption` | string | Optional caption |

### Response `201 Created`

```json
{
  "photoId": "photo_abc123",
  "url": "https://cdn.carditrack.com/alert-photos/photo_abc123.jpg",
  "caption": "Doctor visit summary",
  "uploadedAt": "2026-03-10T14:05:00Z"
}
```

---

## GET `/api/v1/alerts/{alertId}/history`

Get historical frequency data for the same alert type on this CardiMember. Provides context for caregivers ("This is the first time this month").

**Priority:** P1 | **Auth Required:** Yes

### Response `200 OK`

```json
{
  "alertId": "alert_xyz_001",
  "type": "no_morning_activity",
  "cardiMemberId": "cm_01J8K2...",
  "history": {
    "last7Days": 0,
    "last30Days": 1,
    "last90Days": 2,
    "frequencyNote": "This is the first time this month.",
    "previousOccurrences": [
      {
        "alertId": "alert_abc_002",
        "triggeredAt": "2026-02-14T09:15:00Z",
        "status": "resolved"
      }
    ]
  }
}
```

---

## GET `/api/v1/cardimembers/{id}/alert-preferences`

Get the alert notification preferences configured for a specific CardiMember.

**Priority:** P1 | **Auth Required:** Yes

### Response `200 OK`

```json
{
  "cardiMemberId": "cm_01J8K2...",
  "sensitivity": "medium",
  "channels": {
    "push": true,
    "email": true,
    "sms": false
  },
  "quietHours": {
    "enabled": true,
    "from": "22:00",
    "to": "07:00",
    "timezone": "America/New_York",
    "overrideForSeverity": ["red"]
  },
  "alertTypeSettings": [
    {
      "type": "activity_decline",
      "enabled": true,
      "minSeverity": "yellow"
    },
    {
      "type": "elevated_heart_rate",
      "enabled": true,
      "minSeverity": "orange"
    },
    {
      "type": "no_morning_activity",
      "enabled": true,
      "minSeverity": "yellow"
    }
  ],
  "familyRoutingRules": [
    {
      "userId": "usr_sibling123",
      "name": "Tom Doe",
      "receivesSeverity": ["red"]
    }
  ]
}
```

---

## PUT `/api/v1/cardimembers/{id}/alert-preferences`

Update alert notification preferences for a CardiMember.

**Priority:** P1 | **Auth Required:** Yes | **Required Role:** Admin, Staff

### Request Body (partial update supported)

```json
{
  "sensitivity": "high",
  "channels": {
    "push": true,
    "email": false,
    "sms": true
  },
  "quietHours": {
    "enabled": true,
    "from": "22:00",
    "to": "07:00",
    "timezone": "America/New_York",
    "overrideForSeverity": ["red"]
  },
  "alertTypeSettings": [
    {
      "type": "activity_decline",
      "enabled": true,
      "minSeverity": "orange"
    }
  ],
  "familyRoutingRules": [
    {
      "userId": "usr_sibling123",
      "receivesSeverity": ["orange", "red"]
    }
  ]
}
```

**Sensitivity Values** (design intent — today only the `medium` thresholds exist, as fixed constants; see "Implemented today"):

| Value | Description |
|-------|-------------|
| `low` | Only trigger alerts on large deviations (>50% from baseline) |
| `medium` | Standard thresholds (>30% deviation) — **current hard-coded behavior** |
| `high` | Sensitive thresholds (>15% deviation) |

### Response `200 OK`

Returns updated preferences object (same schema as GET).

---

**Related:** [readme.md](readme.md) | [notifications.md](notifications.md) | [family.md](family.md) | [User Stories 3.1, 3.2, 3.3, 11.1–11.3](../../ui/mobile/user_stories.md)

**Last Updated:** August 9, 2026
