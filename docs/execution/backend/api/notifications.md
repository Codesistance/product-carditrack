# Notifications API

> **Status: Partially implemented.** The **in-app data-completeness inbox ships** (see "Implemented today"). Push delivery — device tokens, FCM/APNs, the delivery outbox, acknowledgement and escalation — is designed but **not built**; those endpoints below remain design intent.

Two things share this path prefix, and they are not the same feature:

1. **Data-completeness notifications** — what CardiTrack is missing from an account, what supplying it unlocks, and the caregiver's comply / snooze / mute response. **Shipped, in-app only.**
2. **Push delivery** for health alerts — device registration, tokens, acknowledgement. **Planned**, alongside alert generation (#111) and the R2 pipeline.

The engine behind both is designed in [notification_engine.md](../../../technical/notification_engine.md). **Email and SMS are out of scope by decision:** escalation runs across recipients and devices, never vendors.

**User Stories:** 3.2 (Managing Alert Notifications), 5.1 (Mobile Push Notifications)

---

## Implemented today

The in-app inbox and its actions. All responses use the standard `ApiResponse<T>` envelope; enums serialize as integers.

| Endpoint | Notes |
|----------|-------|
| `GET /api/v1/notifications` | The caller's inbox, priority-ranked. Query params `state`, `category`, `cardiMemberId`, `owned`, `limit` (default 50, max 200), `offset`. Unrecognised `state`/`category` values are rejected with **400** rather than silently ignored |
| `GET /api/v1/notifications/summary` | Unseen count, safety banners and the two dashboard card slots in one call — what the app reads on launch |
| `POST /api/v1/notifications/{id}/seen` | Records first sighting; idempotent, and only the first counts. Drives the comply funnel's denominator |
| `POST /api/v1/notifications/{id}/snooze` | Body `{ "duration": "7.00:00:00" }`, optional. **Clamped** to the rule's maximum rather than rejected, so a client asking for a month on a safety rule gets 72 hours and a success |
| `POST /api/v1/notifications/{id}/dismiss` | Body `{ "acknowledgedConsequence": bool }`. Writes a mute and resolves the row. Safety-class rules **require** the acknowledgement and return **400** without it |
| `GET /api/v1/notifications/mutes` | Everything the caller has silenced — the settings screen's list |
| `DELETE /api/v1/notifications/mutes/{muteId}` | Un-mute one rule; anything still outstanding reappears immediately |
| `POST /api/v1/notifications/mutes/reset` | "Show me everything again" |

Every action is idempotent, and a notification belonging to another user returns **404** rather than 403 — the same non-disclosure convention as [alerts.md](alerts.md).

Seen / snooze / dismiss are **owner-only**. A relative's copy of a family notification (`isOwner: false`) is returned by the inbox so the rest of a family can see something is outstanding, but acting on it returns **404** as well: the card hides the buttons, and the API refuses the call regardless of what the client sends.

### Notification shape

```json
{
  "id": "9b2f5f64-5717-4562-b3fc-2c963f66afa6",
  "ruleCode": "DEVICE_STALE_LONG",
  "category": 2,
  "priority": 2,
  "state": 1,
  "titleKey": "nudge.DEVICE_STALE_LONG.title",
  "bodyKey": "nudge.DEVICE_STALE_LONG.body",
  "benefitKey": "nudge.DEVICE_STALE_LONG.benefit",
  "templateData": "{\"hours\":52}",
  "cardiMemberId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "cardiMemberName": "Margaret",
  "actionDeepLink": "carditrack://cardimembers/3fa85f64-.../devices",
  "canMute": true,
  "maxSnoozeHours": 336,
  "isOwner": true,
  "snoozedUntil": null,
  "firstDetectedDate": "2026-08-10T06:00:00Z",
  "firstSeenDate": null
}
```

Two things about this payload are deliberate:

- **Copy is keys, not sentences.** The client owns the words, so wording and translation change in an app release rather than a data migration, and a notification raised last month renders in today's copy.
- **`cardiMemberName` is resolved per request**, never stored on the row. `templateData` carries counters only — a wearer's name persisted beside the health-derived gap describing them would be an identifier-to-clinical join in the clear ([data_protection_architecture.md](../../../technical/data_protection_architecture.md) §2).

`isOwner` is false for relatives who can see an item somebody else is responsible for: visible so the family knows it is outstanding, never actionable, so one missing emergency contact does not nag five people.

### Related — implemented alongside

`PUT /api/v1/users/me/timezone` — body `{ "timeZoneId": "Europe/London" }`. Added with the engine because the `TIMEZONE_DEFAULT` notification asks the user to set it and nothing could. Rejects ids the platform does not recognise with **400**.

### Not implemented

No push infrastructure exists: no device-token entity, no FCM/APNs integration, no sender, no delivery outbox. The `UserCardiMember` link still carries a `NotificationPreferences` JSON column and a `ReceiveAlerts` flag; the flag is read by targeting, the JSON column by nothing. It belongs to health-alert routing (R2) and is deliberately untouched by the in-app engine.

Everything below is the **planned** push contract, kept as design intent.

---

## POST `/api/v1/notifications/devices`

Register a device push notification token for the authenticated user. Called on mobile app launch after the user grants notification permission.

**Priority:** P0 | **Auth Required:** Yes

### Request Body

```json
{
  "deviceId": "device_abc123",
  "platform": "ios",
  "pushToken": "apns_token_abc123xyz...",
  "appVersion": "2.0.1"
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `deviceId` | string | Yes | Unique device identifier (stable across app reinstalls) |
| `platform` | string | Yes | `"ios"` or `"android"` |
| `pushToken` | string | Yes | APNS (iOS) or FCM (Android) push token |
| `appVersion` | string | No | Installed app version for diagnostics |

### Response `201 Created`

```json
{
  "tokenId": "pnt_xyz789",
  "deviceId": "device_abc123",
  "platform": "ios",
  "registeredAt": "2026-03-09T10:00:00Z"
}
```

> If the device is already registered, the push token is updated (upsert behavior) and `200 OK` is returned instead of `201`.

### Response `200 OK` (token updated)

```json
{
  "tokenId": "pnt_xyz789",
  "deviceId": "device_abc123",
  "platform": "ios",
  "updatedAt": "2026-03-09T10:00:00Z"
}
```

---

## DELETE `/api/v1/notifications/devices/{tokenId}`

Unregister a push notification device. Call this on logout or when the user disables push notifications.

**Priority:** P0 | **Auth Required:** Yes

### Path Parameters

| Parameter | Description |
|-----------|-------------|
| `tokenId` | Push notification token ID |

### Response `204 No Content`

### Errors

| Code | Status | Description |
|------|--------|-------------|
| `TOKEN_NOT_FOUND` | 404 | Token ID not found or does not belong to the user |

---

## GET `/api/v1/notifications/preferences`

Get the authenticated user's global notification preferences across all CardiMembers.

**Priority:** P1 | **Auth Required:** Yes

### Response `200 OK`

```json
{
  "userId": "usr_01J8K2...",
  "globalChannels": {
    "push": true,
    "email": true,
    "sms": false
  },
  "weeklyDigest": {
    "enabled": true,
    "deliveryDay": "monday",
    "deliveryTime": "08:00",
    "timezone": "America/New_York"
  },
  "registeredDevices": [
    {
      "tokenId": "pnt_xyz789",
      "deviceId": "device_abc123",
      "platform": "ios",
      "lastSeenAt": "2026-03-09T10:00:00Z"
    }
  ]
}
```

> Per-CardiMember alert preferences (quiet hours, sensitivity, routing rules) are designed as `GET /api/v1/cardimembers/{id}/alert-preferences` in [alerts.md](alerts.md) — also planned, not yet implemented.

---

## PUT `/api/v1/notifications/preferences`

Update the authenticated user's global notification preferences.

**Priority:** P1 | **Auth Required:** Yes

### Request Body (partial update supported)

```json
{
  "globalChannels": {
    "push": true,
    "email": false,
    "sms": true
  },
  "weeklyDigest": {
    "enabled": true,
    "deliveryDay": "sunday",
    "deliveryTime": "09:00",
    "timezone": "America/Chicago"
  }
}
```

| Field | Type | Description |
|-------|------|-------------|
| `globalChannels.push` | boolean | Enable/disable push notifications for all alerts |
| `globalChannels.email` | boolean | Enable/disable email notifications for all alerts |
| `globalChannels.sms` | boolean | Enable/disable SMS notifications for all alerts |
| `weeklyDigest.enabled` | boolean | Enable weekly health summary email |
| `weeklyDigest.deliveryDay` | string | Day of week: `monday`–`sunday` |
| `weeklyDigest.deliveryTime` | string | Time in `HH:mm` format (24h) |
| `weeklyDigest.timezone` | string | IANA timezone string |

### Response `200 OK`

Returns the updated preferences object (same schema as GET).

---

**Push Notification Payload Structure**

Rich push notifications sent by the backend include action buttons to allow caregivers to respond without opening the app.

```json
{
  "title": "Margaret hasn't moved today",
  "body": "Typical wake time: 7am. Current time: 11am.",
  "data": {
    "type": "alert",
    "alertId": "alert_xyz_001",
    "cardiMemberId": "cm_01J8K2...",
    "severity": "red",
    "deepLink": "carditrack://alerts/alert_xyz_001"
  },
  "actions": [
    { "id": "call", "title": "Call Now" },
    { "id": "acknowledge", "title": "Acknowledge" }
  ],
  "badge": 3
}
```

---

**Related:** [readme.md](readme.md) | [alerts.md](alerts.md) | [User Stories 3.2, 5.1](../../ui/mobile/user_stories.md)

**Last Updated:** August 7, 2026
