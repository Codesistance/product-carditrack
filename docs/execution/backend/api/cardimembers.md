# CardiMembers API

> **Status: Partially implemented.** Get-by-id, update, delete and monitoring pause/resume now exist on `/api/v1/cardimembers`. Consent, self-authored notes, photos and plan-limit enforcement remain design intent. See "Implemented today" for current coverage.

Manages the elderly individuals being monitored (CardiMembers), their consent settings, monitoring pause state, and context notes entered on their behalf.

**User Stories:** 1.2 (Adding First CardiMember), 7.1 (Consent & Transparency), 7.2 (Viewing Own Data), 7.3 (Pausing Monitoring)

> **Wearer-audience stories are descoped.** Wearers never log in (product decision 2026-08-10 — self-monitoring is not the objective), so the wearer-facing parts of 7.2/7.3 will never ship: pause/resume is a caregiver action, and "own data"/"own account" framing below is retained only as historical design intent.

---

## Implemented today

CardiMember create/list currently lives on the **Onboarding** controller (note the `/api/Onboarding` route — no `v1` segment):

### POST `/api/Onboarding/cardimember`

Creates a CardiMember in the caller's organization (organization comes from the authenticated user context, never the body). Returns **201** with the member wrapped in the standard `ApiResponse<T>` envelope. Returns **403** if the caller has no organization yet ("set up your organization first"), **400** with field-level `errors` on validation failure.

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `name` | string | **Yes** | 2–100 chars |
| `dateOfBirth` | date (`DateOnly`) | **Yes** | Member must validate as 18–120 years old |
| `gender` | integer enum | **Yes** | `[Required]` — sex is captured at onboarding (M1-04), a deliberate divergence from the Figma comps, because the reference ranges the prompt layer reads depend on it |
| `email` | string | No | Validated for email format |
| `phone` | string | No | Validated as a phone number |
| `emergencyContactName` | string | No | ≤ 100 chars |
| `emergencyContactPhone` | string | No | Validated as a phone number |
| `medicalNotes` | string | No | ≤ 2000 chars; encrypted at rest |
| `relationshipType` | integer enum | No | Defaults to `Other` (99) — not knowing how you are related is no reason to be unable to start watching over someone |
| `isPrimaryCaregiver` | boolean | No | Defaults to `true` |

### GET `/api/Onboarding/cardimembers`

Returns **200** with a plain list of the organization's CardiMembers — **no sorting, filtering, or `total` count**. Returns **403** if the caller has no organization.

### Actual `CardiMemberResponse` shape

```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "name": "Margaret Doe",
  "dateOfBirth": "1945-06-15",
  "age": 80,
  "gender": 2,
  "email": "margaret@example.com",
  "phone": "+15551234567",
  "relationship": 2,
  "isPrimaryCaregiver": false,
  "isActive": true,
  "createdDate": "2026-01-15T09:00:00Z"
}
```

- `id` is a **raw GUID** — no `cm_` prefix.
- `gender` and `relationship` are **integer enums** (`Gender`: Male=1, Female=2, PreferNotToSay=4; `RelationshipType`: Self=1, Parent=2, Spouse=3, Grandparent=4, Sibling=5, Child=6, Other=99). **3 is retired** — it was `Other`, and is now rejected by both validators; the members holding it were migrated to `PreferNotToSay` by the `RetireOtherGender` migration. The mobile form offers only Male and Female; `PreferNotToSay` remains readable because it is the stored value for every member created before M1-04 asked.
- The **list** response deliberately carries no `medicalNotes` or emergency contact. Those are PHI and are served only by the single-member GET below, so a "which members do I have?" call never broadcasts them.

### GET `/api/v1/cardimembers/{id}`

Full detail for one CardiMember — the payload behind mobile M1-13. Requires **view** access (an active `UserCardiMember` link with `CanViewHealthData`). Returns **200**, or **404** when the member does not exist *or* the caller may not see them — the two are deliberately indistinguishable.

```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "name": "Margaret Doe",
  "dateOfBirth": "1945-06-15",
  "age": 80,
  "gender": 2,
  "email": "margaret@example.com",
  "phone": "+15551234567",
  "relationship": 2,
  "isPrimaryCaregiver": true,
  "emergencyContactName": "Jane Doe",
  "emergencyContactPhone": "+15551234568",
  "medicalNotes": "Type 2 diabetes, takes metformin",
  "photoUrl": null,
  "alertSensitivity": 2,
  "monitoringPaused": false,
  "monitoringPausedUntil": null,
  "monitoringPauseReason": null,
  "monitoringSince": "2026-01-15T09:00:00Z",
  "lastSyncedAt": "2026-03-09T08:30:00Z",
  "connectedDeviceCount": 2,
  "dataFreshness": "green",
  "dataFreshnessMessage": "Data processed",
  "baseline": { "isLearning": true, "daysCaptured": 15, "daysRequired": 30, "percentComplete": 50 },
  "healthStatus": "green",
  "metrics": { "steps": { "…": "…" }, "restingHeartRate": { "…": "…" }, "sleep": { "…": "…" }, "temperature": { "…": "…" }, "spO2": { "…": "…" }, "breathingRate": { "…": "…" } }
}
```

- `relationship` is the **requesting caregiver's own** link, not the first link stored against the member.
- `dataFreshness` / `dataFreshnessMessage` are the same deterministic pipeline-freshness pair as the dashboard (`red` / `amber` / `blue` / `green`) — see [health-data.md](health-data.md). M1-13 renders them as a coloured dot in front of the last-contact age.
- `healthStatus` is the same lowercase string, computed the same way, as the dashboard's — see [health-data.md](health-data.md).
- `metrics` is the full `DashboardMetrics` block (each metric with its 30-day series) so the detail screen's trend cards need no second round-trip; it is **`null`** when the member has no activity history yet, the same condition the dashboard uses.
- `medicalNotes` is stored AES-256-GCM encrypted and decrypted on read. Rows written before encryption was introduced are returned as-is rather than failing the request.
- `photoUrl` is always `null` — no photo storage exists. Clients render an initials avatar.
- `alertSensitivity` is an integer enum (Low=1, Medium=2, High=3). **Stored but not consumed** — statistical alerting uses the established 30-day baseline, not this field.

### PUT `/api/v1/cardimembers/{id}`

Saves the M1-14 edit form. Requires **manage** access (as above, plus `IsPrimaryCaregiver`). A **full replacement**, not a patch: omitting a field clears it. Returns the updated detail object, **400** with field errors, or **404**.

Body: `name`, `dateOfBirth`, `gender`, `relationshipType`, `email`, `phone`, `emergencyContactName`, `emergencyContactPhone`, `medicalNotes`, `alertSensitivity`. `relationshipType` updates the caller's own link only, so it cannot rewrite what other caregivers call this person.

`gender` is the **one exception to full replacement**: it is nullable, and omitting it leaves the stored value alone rather than clearing it. This is what lets a caller that does not render the sex picker — an older build, or any edit to a phone number — save the form without silently discarding a stated sex and the reference range the prompt layer reads from it. Sending an explicit `0` is a client bug and is rejected; to say "not recorded", send `4`.

### DELETE `/api/v1/cardimembers/{id}`

Removes a CardiMember. Requires **manage** access. Returns **204**. Soft delete: the member, their caregiver links and their device connections are deactivated and stored OAuth tokens discarded. Health history is retained.

### POST `/api/v1/cardimembers/{id}/pause` · DELETE `/api/v1/cardimembers/{id}/pause`

Pauses and resumes monitoring. Requires **manage** access. Body on POST: `durationHours` (1–168, required) and optional `reason` (≤200 chars). Both return `{ monitoringPaused, monitoringPausedUntil, monitoringPauseReason }`.

The pause is **time-bounded on purpose** — an open-ended pause would let someone stop being monitored indefinitely with nobody deciding to — and it expires on its own, with no job required to lift it. It is enforced, not cosmetic: `GetDueForSyncAsync` excludes paused members so the sync worker skips them, and the dashboard reports `healthStatus: "paused"` rather than a health colour.

- `familyNotified` from the planned contract below is **not implemented** — there is no family notification path yet.

### Not yet built

- **Plan-limit enforcement**: subscription `MaxCardiMembers` exists on the entity but **nothing enforces it** — the `CARDIMEMBER_LIMIT_REACHED` error below does not occur.
- **Consent, self-authored notes, photos**: no entities or endpoints exist for any of these.
- **`cm_`-prefixed ids, `sort`/`filter` query parameters, `total` counts, string enums**: the shapes below use them; the implementation does not.

Everything below is the **planned** contract, kept as design intent.

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

**Priority:** P2 | **Auth Required:** Yes (caregiver with manage access — wearers never log in)

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

Get context notes about the CardiMember (e.g. "She was sick this week"). Originally designed as self-authored; since wearers never log in, any notes feature would be **caregiver-entered on the member's behalf**.

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

Add a context note about the CardiMember. (Originally "as the CardiMember" — descoped: wearers never log in, so this would be a caregiver writing on their behalf.)

**Priority:** P2 | **Auth Required:** Yes

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

**Last Updated:** August 14, 2026
