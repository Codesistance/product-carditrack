# Alerts API

Handles alert retrieval, acknowledgment, status lifecycle, photo attachments, and per-member alert notification preferences including quiet hours and sensitivity.

**User Stories:** 3.1 (Receiving Critical Alerts), 3.2 (Managing Alert Notifications), 3.3 (Alert Acknowledgment & Notes), 11.1 (Activity Alerts), 11.2 (Heart Rate Alerts), 11.3 (Pattern Break Alerts)

---

## GET `/api/v1/alerts`

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

**Alert Types:**

| Type | Severity Range | Description |
|------|---------------|-------------|
| `activity_decline` | yellow | Gradual step/activity reduction |
| `elevated_heart_rate` | orange | Resting HR above normal range |
| `no_morning_activity` | red | No movement detected past typical wake time (device is syncing) |
| `irregular_sleep` | yellow | Sleep duration significantly off baseline |
| `device_disconnected` | yellow | Wearable not syncing (>2h silence during waking hours) |
| `long_term_trend` | orange | Multi-week decline trend (e.g. steps −5%/week for 4 weeks) — *ships with the AI pipeline, see [release matrix](../../../release_matrix.md)* |

> Severities use the product taxonomy (`yellow`/`orange`/`red`). `green` is a *health status*, not an alert severity — no alert is emitted for normal states. The AI pipeline's internal Critical/High/Medium/Low scale maps to these values — see [llm_design.md](../../../llm_design.md).

---

## GET `/api/v1/cardimembers/{id}/alerts`

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

**Sensitivity Values:**

| Value | Description |
|-------|-------------|
| `low` | Only trigger alerts on large deviations (>50% from baseline) |
| `medium` | Standard thresholds (>30% deviation) |
| `high` | Sensitive thresholds (>15% deviation) |

### Response `200 OK`

Returns updated preferences object (same schema as GET).

---

**Related:** [readme.md](readme.md) | [notifications.md](notifications.md) | [family.md](family.md) | [User Stories 3.1, 3.2, 3.3, 11.1–11.3](../../ui/mobile/user_stories.md)
