# CardiMembers API

Manages the elderly individuals being monitored (CardiMembers), their consent settings, monitoring pause state, and self-authored notes.

**User Stories:** 1.2 (Adding First CardiMember), 7.1 (Consent & Transparency), 7.2 (Viewing Own Data), 7.3 (Pausing Monitoring)

---

## GET `/api/v1/cardimembers`

List all CardiMembers associated with the authenticated user's account.

**Priority:** P0 | **Auth Required:** Yes

### Query Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `sort` | string | `"name"` or `"status"` (default: `"status"`) |
| `filter` | string | `"alerts"` — show only members with active alerts |

### Response `200 OK`

```json
{
  "cardimembers": [
    {
      "id": "cm_01J8K2...",
      "name": "Margaret Doe",
      "dateOfBirth": "1945-06-15",
      "relationship": "Mother",
      "photoUrl": "https://cdn.carditrack.com/photos/cm_01J8K2.jpg",
      "healthStatus": "yellow",
      "lastSyncedAt": "2026-03-09T08:30:00Z",
      "monitoringPaused": false,
      "activeAlertCount": 1
    }
  ],
  "total": 1
}
```

---

## POST `/api/v1/cardimembers`

Create a new CardiMember. Uses progressive disclosure — only required fields needed at creation.

**Priority:** P0 | **Auth Required:** Yes

### Request Body

```json
{
  "name": "Margaret Doe",
  "dateOfBirth": "1945-06-15",
  "relationship": "Mother",
  "photoBase64": "data:image/jpeg;base64,/9j/4AAQ...",
  "medicalNotes": "Type 2 diabetes, takes metformin",
  "emergencyContacts": [
    {
      "name": "John Doe",
      "phone": "+15551234567",
      "relationship": "Son"
    }
  ]
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `name` | string | Yes | Full name |
| `dateOfBirth` | string (ISO 8601) | Yes | Date of birth |
| `relationship` | string | Yes | Caregiver's relationship to member |
| `photoBase64` | string | No | Profile photo (JPEG/PNG, max 5MB) |
| `medicalNotes` | string | No | Encrypted at rest |
| `emergencyContacts` | array | No | Up to 5 contacts |

### Response `201 Created`

```json
{
  "id": "cm_01J8K2...",
  "name": "Margaret Doe",
  "dateOfBirth": "1945-06-15",
  "relationship": "Mother",
  "photoUrl": "https://cdn.carditrack.com/photos/cm_01J8K2.jpg",
  "healthStatus": "unknown",
  "monitoringPaused": false,
  "createdAt": "2026-03-09T10:00:00Z"
}
```

### Errors

| Code | Status | Description |
|------|--------|-------------|
| `CARDIMEMBER_LIMIT_REACHED` | 422 | Plan tier limit exceeded |
| `INVALID_DATE_OF_BIRTH` | 400 | DOB is in the future or invalid |

---

## GET `/api/v1/cardimembers/{id}`

Get full details for a single CardiMember.

**Priority:** P0 | **Auth Required:** Yes

### Path Parameters

| Parameter | Description |
|-----------|-------------|
| `id` | CardiMember ID |

### Response `200 OK`

```json
{
  "id": "cm_01J8K2...",
  "name": "Margaret Doe",
  "dateOfBirth": "1945-06-15",
  "relationship": "Mother",
  "photoUrl": "https://cdn.carditrack.com/photos/cm_01J8K2.jpg",
  "medicalNotes": "Type 2 diabetes, takes metformin",
  "emergencyContacts": [...],
  "healthStatus": "yellow",
  "monitoringPaused": false,
  "monitoringPausedUntil": null,
  "baselineLearningProgress": {
    "daysCaptured": 12,
    "daysRequired": 30,
    "percentComplete": 40
  },
  "consentSettings": {
    "shareActivity": true,
    "shareHeartRate": true,
    "shareSleep": true,
    "consentedAt": "2026-01-15T09:00:00Z"
  },
  "lastSyncedAt": "2026-03-09T08:30:00Z",
  "createdAt": "2026-01-15T09:00:00Z"
}
```

### Errors

| Code | Status | Description |
|------|--------|-------------|
| `CARDIMEMBER_NOT_FOUND` | 404 | ID does not exist or not accessible |

---

## PUT `/api/v1/cardimembers/{id}`

Update CardiMember details.

**Priority:** P0 | **Auth Required:** Yes | **Required Role:** Admin, Staff

### Request Body (partial update supported)

```json
{
  "name": "Margaret A. Doe",
  "medicalNotes": "Type 2 diabetes, takes metformin. Now also on lisinopril.",
  "photoBase64": "data:image/jpeg;base64,/9j/4AAQ..."
}
```

### Response `200 OK`

Returns the updated CardiMember object (same schema as GET).

---

## DELETE `/api/v1/cardimembers/{id}`

Remove a CardiMember. Requires Admin role. Historical health data is retained for 90 days.

**Priority:** P1 | **Auth Required:** Yes | **Required Role:** Admin

### Response `204 No Content`

### Errors

| Code | Status | Description |
|------|--------|-------------|
| `INSUFFICIENT_PERMISSIONS` | 403 | Only Admins can delete CardiMembers |

---

## POST `/api/v1/cardimembers/{id}/consent`

Record or update the CardiMember's consent preferences for what data types are shared.

**Priority:** P0 | **Auth Required:** Yes

### Request Body

```json
{
  "shareActivity": true,
  "shareHeartRate": true,
  "shareSleep": false,
  "consentedByName": "Margaret Doe",
  "consentMethod": "digital_signature"
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `shareActivity` | boolean | Yes | Consent to share step/activity data |
| `shareHeartRate` | boolean | Yes | Consent to share heart rate data |
| `shareSleep` | boolean | Yes | Consent to share sleep data |
| `consentedByName` | string | Yes | Name of consenting person |
| `consentMethod` | string | Yes | `"digital_signature"` or `"verbal_confirmed"` |

### Response `200 OK`

```json
{
  "consentSettings": {
    "shareActivity": true,
    "shareHeartRate": true,
    "shareSleep": false,
    "consentedAt": "2026-03-09T10:00:00Z",
    "consentedByName": "Margaret Doe"
  }
}
```

---

## POST `/api/v1/cardimembers/{id}/pause`

Temporarily pause monitoring for a CardiMember. All connected family members are notified.

**Priority:** P2 | **Auth Required:** Yes (CardiMember's own account or Admin)

### Request Body

```json
{
  "durationHours": 24,
  "reason": "Travelling — no device"
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `durationHours` | integer | Yes | Hours to pause (1–168) |
| `reason` | string | No | Optional reason shown to family |

### Response `200 OK`

```json
{
  "monitoringPaused": true,
  "monitoringPausedUntil": "2026-03-10T10:00:00Z",
  "familyNotified": true
}
```

---

## DELETE `/api/v1/cardimembers/{id}/pause`

Resume monitoring before the scheduled auto-resume time.

**Priority:** P2 | **Auth Required:** Yes

### Response `200 OK`

```json
{
  "monitoringPaused": false,
  "resumedAt": "2026-03-09T14:00:00Z"
}
```

---

## GET `/api/v1/cardimembers/{id}/notes`

Get self-authored notes from the CardiMember (e.g. "I was sick this week").

**Priority:** P2 | **Auth Required:** Yes

### Response `200 OK`

```json
{
  "notes": [
    {
      "id": "note_abc",
      "content": "I was sick this week, that's why activity is low",
      "createdAt": "2026-03-07T18:00:00Z"
    }
  ]
}
```

---

## POST `/api/v1/cardimembers/{id}/notes`

Add a self-authored note as the CardiMember.

**Priority:** P2 | **Auth Required:** Yes (CardiMember's own account)

### Request Body

```json
{
  "content": "Had a cold this week, resting more than usual."
}
```

### Response `201 Created`

```json
{
  "id": "note_xyz",
  "content": "Had a cold this week, resting more than usual.",
  "createdAt": "2026-03-09T10:00:00Z"
}
```

### Errors

| Code | Status | Description |
|------|--------|-------------|
| `NOTE_TOO_LONG` | 400 | Content exceeds 1000 characters |

---

**Related:** [readme.md](readme.md) | [devices.md](devices.md) | [User Stories 1.2, 7.1–7.3](../../ui/mobile/user_stories.md)
