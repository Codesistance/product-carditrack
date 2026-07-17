# Device Management API

Handles wearable device connections via OAuth, device status management, primary device designation, and token refresh.

**User Stories:** 1.3 (Device Connection Wizard), 6.2 (Device Management)

---

## GET `/api/v1/cardimembers/{id}/devices`

List all wearable devices connected to a CardiMember.

**Priority:** P0 | **Auth Required:** Yes

### Path Parameters

| Parameter | Description |
|-----------|-------------|
| `id` | CardiMember ID |

### Response `200 OK`

```json
{
  "devices": [
    {
      "deviceId": "dev_01J9...",
      "provider": "fitbit",
      "displayName": "Fitbit Charge 6",
      "status": "active",
      "isPrimary": true,
      "lastSyncedAt": "2026-03-09T08:30:00Z",
      "connectedAt": "2026-01-15T09:00:00Z",
      "tokenExpiresAt": "2026-06-09T09:00:00Z"
    },
    {
      "deviceId": "dev_02J9...",
      "provider": "garmin",
      "displayName": "Garmin Venu 3",
      "status": "token_expired",
      "isPrimary": false,
      "lastSyncedAt": "2026-02-01T10:00:00Z",
      "connectedAt": "2025-12-01T09:00:00Z",
      "tokenExpiresAt": "2026-02-01T09:00:00Z"
    }
  ]
}
```

**Device Status Values:**

| Status | Description |
|--------|-------------|
| `active` | Syncing normally |
| `disconnected` | OAuth connection removed |
| `token_expired` | OAuth token needs refresh |
| `pending` | OAuth flow not yet completed |

---

## POST `/api/v1/cardimembers/{id}/devices`

Initiate an OAuth device connection. Returns a redirect URL for the provider's authorization page.

**Priority:** P0 | **Auth Required:** Yes

### Request Body

```json
{
  "provider": "fitbit",
  "redirectUri": "carditrack://oauth/callback"
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `provider` | string | Yes | A **server-OAuth** provider: `fitbit`, `garmin`, `samsung_health`, `withings`. (`apple_health` uses the on-device bridge — see below — and is not valid here.) |
| `redirectUri` | string | Yes | Deep link URI for mobile callback |

### Response `200 OK`

```json
{
  "authorizationUrl": "https://www.fitbit.com/oauth2/authorize?client_id=...",
  "state": "csrf_state_token_abc123",
  "codeVerifier": "pkce_verifier_xyz"
}
```

> The client stores `codeVerifier` and `state` locally, then redirects the user to `authorizationUrl`. After authorization, the provider redirects to `redirectUri` with a `code` and `state` parameter, which is sent to the OAuth callback endpoint.

---

## POST `/api/v1/oauth/callback/{provider}`

OAuth callback completion. After the provider redirects the client back to `redirectUri` with `code` and `state` query parameters, the client POSTs them (with the locally stored PKCE verifier) to this endpoint, which exchanges the code for tokens and stores the connection.

**Priority:** P0 | **Auth Required:** Yes

> The `code_verifier` is sent in the **request body over an authenticated POST** — never as a URL query parameter, where it would be exposed to proxy/CDN logs and browser history.

### Path Parameters

| Parameter | Description |
|-----------|-------------|
| `provider` | OAuth provider name (e.g. `fitbit`) |

### Request Body

```json
{
  "code": "authorization_code_from_provider",
  "state": "csrf_state_token_abc123",
  "codeVerifier": "pkce_verifier_xyz"
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `code` | string | Yes | Authorization code from provider |
| `state` | string | Yes | CSRF state token (must match the value issued at initiation) |
| `codeVerifier` | string | Yes | PKCE verifier stored client-side at initiation |

### Response `201 Created`

```json
{
  "deviceId": "dev_01J9...",
  "provider": "fitbit",
  "displayName": "Fitbit Charge 6",
  "status": "active",
  "isPrimary": false,
  "connectedAt": "2026-03-09T10:00:00Z"
}
```

### Errors

| Code | Status | Description |
|------|--------|-------------|
| `INVALID_STATE_TOKEN` | 400 | CSRF state mismatch |
| `OAUTH_EXCHANGE_FAILED` | 502 | Provider rejected code exchange |
| `PROVIDER_PERMISSION_DENIED` | 403 | User denied required scopes |

---

## GET `/api/v1/cardimembers/{id}/devices/{deviceId}`

Get details and current status for a single connected device.

**Priority:** P1 | **Auth Required:** Yes

### Response `200 OK`

```json
{
  "deviceId": "dev_01J9...",
  "provider": "fitbit",
  "displayName": "Fitbit Charge 6",
  "status": "active",
  "isPrimary": true,
  "scopes": ["activity", "heartrate", "sleep"],
  "lastSyncedAt": "2026-03-09T08:30:00Z",
  "connectedAt": "2026-01-15T09:00:00Z",
  "tokenExpiresAt": "2026-06-09T09:00:00Z"
}
```

### Errors

| Code | Status | Description |
|------|--------|-------------|
| `DEVICE_NOT_FOUND` | 404 | Device ID not found for this CardiMember |

---

## PUT `/api/v1/cardimembers/{id}/devices/{deviceId}/primary`

Set this device as the primary data source. Clears primary flag from any previously primary device.

**Priority:** P1 | **Auth Required:** Yes

### Response `200 OK`

```json
{
  "deviceId": "dev_01J9...",
  "isPrimary": true,
  "updatedAt": "2026-03-09T10:00:00Z"
}
```

---

## POST `/api/v1/cardimembers/{id}/devices/{deviceId}/reconnect`

Initiate a token refresh for a device with an expired or revoked OAuth token.

**Priority:** P1 | **Auth Required:** Yes

### Request Body

```json
{
  "redirectUri": "carditrack://oauth/callback"
}
```

### Response `200 OK`

```json
{
  "authorizationUrl": "https://www.fitbit.com/oauth2/authorize?client_id=...",
  "state": "csrf_state_token_def456",
  "codeVerifier": "pkce_verifier_new"
}
```

> Follows the same PKCE OAuth flow as initial connection.

---

## DELETE `/api/v1/cardimembers/{id}/devices/{deviceId}`

Remove a device connection. Historical data synced via this device is retained.

A CardiMember **may have zero connected devices** (e.g. between switching devices). In that state their `healthStatus` becomes `unknown`, health-summary endpoints return `NO_DEVICE_CONNECTED`, and family members are notified that monitoring is inactive. If the deleted device was primary, the oldest remaining active device (if any) becomes primary.

**Priority:** P1 | **Auth Required:** Yes | **Required Role:** Admin, Staff

### Response `204 No Content`

### Errors

| Code | Status | Description |
|------|--------|-------------|
| `DEVICE_NOT_FOUND` | 404 | Device ID not found for this CardiMember |

---

**Supported Providers:**

| Provider | `provider` Value | Integration Mode | Scopes / Permissions |
|----------|-----------------|------------------|----------------------|
| Fitbit | `fitbit` | `server_oauth` | `activity`, `heartrate`, `sleep` |
| Apple Health | `apple_health` | `on_device_bridge` | `HKQuantityTypeStepCount`, `HKQuantityTypeHeartRate`, `HKCategoryTypeAsleepCore` |
| Garmin | `garmin` | `server_oauth` | `activities`, `heart_rate`, `sleep` |
| Samsung Health | `samsung_health` | `server_oauth` | `steps`, `heart_rate`, `sleep` |
| Withings | `withings` | `server_oauth` | `user.metrics` |

> **Integration modes:**
> - **`server_oauth`** — CardiTrack's backend holds OAuth tokens and receives data via the provider's cloud API/webhooks.
> - **`on_device_bridge`** (Apple Health) — HealthKit has **no server-side OAuth**. Permissions are granted on the CardiMember's iPhone; the CardiTrack mobile app reads HealthKit locally and uploads normalized samples to `POST /api/v1/cardimembers/{id}/health-data/batch` (device-bridge ingestion). The connection record exists for status/primary tracking, but has no tokens and cannot use the OAuth endpoints above.

---

**Related:** [readme.md](readme.md) | [health-data.md](health-data.md) | [User Stories 1.3, 6.2](../../ui/mobile/user_stories.md)
