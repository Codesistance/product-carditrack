# Notifications API

Manages push notification device token registration and global notification preferences. Alert-specific preferences (quiet hours, sensitivity, routing) are managed via the [Alerts API](alerts.md).

**User Stories:** 3.2 (Managing Alert Notifications), 5.1 (Mobile Push Notifications)

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

> Per-CardiMember alert preferences (quiet hours, sensitivity, routing rules) are managed via `GET /api/v1/cardimembers/{id}/alert-preferences` in [alerts.md](alerts.md).

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
