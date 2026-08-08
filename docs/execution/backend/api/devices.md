# Device Management API

Handles wearable device connections via OAuth, device status management, primary device designation, and token refresh.

**Implementation status:** the core OAuth connection flow (list, connect, bounce redirect, callback) is **implemented**, as are the M1-15 management endpoints — **delete**, **set primary**, and a **refresh** endpoint — plus an on-demand **sync** endpoint (issue #67). Get-single-device remains **planned — not yet implemented**; note the implemented routes differ from the planned shapes below (`POST .../primary` not `PUT`, `POST .../refresh` not `POST .../reconnect`).

Key implementation facts (verified against `DeviceConnectionService`):

- **Authorization is two-tier, member-link based** (failure → 404 "CardiMember not found" in both tiers, so an unauthorised caller can't tell a member exists). *Reading and connecting* — list, initiate, callback — need only an **active `UserCardiMember` link**. The *management* actions that change how a member is monitored — **delete, set-primary, refresh** — additionally require **`IsPrimaryCaregiver`**, so a relative invited only to watch over someone cannot cut off their data feed. **Sync** sits in the reading tier: it changes nothing about the connection and shows the caller nothing they could not already see. There are no Auth0 **role** checks on any device endpoint.
- **State tokens are single-use with a 15-minute TTL**, held server-side in the distributed cache keyed to the initiating user, member, and provider. The callback consumes the state even if the code exchange fails — a replayed state always fails.
- **Google authorize URLs include `access_type=offline`** (config-driven), without which Google issues no refresh token. `prompt=consent` (`FirstConsentAuthorizationParams`) is added **only while the member holds no refresh token** on that provider — Google re-issues one only when consent is shown again, but forcing it on every connect makes a reconnect look like a failure. A token exchange that returns no refresh token **leaves the stored one in place** rather than nulling it — unless the exchange came back with a **different `providerUserId`**, in which case the old account's token is dropped so background syncs can't keep pulling the previous wearer's data (and the next initiation re-prompts for consent).
- **OAuth tokens are AES-encrypted at rest** before being stored on the connection record.
- **Syncing is a cron poll** by the Worker (`WearableSyncWorker`) every 30 minutes, not provider webhooks. The cron sets only how often the worker *looks*; a connection is actually due once its own `SyncFrequencyMinutes` (default 30) has elapsed. Connections belonging to a **removed or monitoring-paused** CardiMember are excluded by `GetDueForSyncAsync`, so a pause genuinely stops collection — see [cardimembers.md](cardimembers.md). Each due connection writes its own raw `DeviceActivityLogs` row, which is then merged into the member's single daily `ActivityLogs` row.
- The anonymous bounce endpoint **only redirects into the `carditrack://` app scheme** — any other cached redirect target is rejected, preventing open-redirect leakage of `code`+`state`.
- **Only Fitbit is actually connectable.** Garmin, Samsung Health, and Withings pass request validation but are **config-only stubs** (placeholder ClientIds, no DI registration) — a connect attempt returns 400 "not configured for connections". Only the Fitbit provider client is registered.

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

Wrapped in the standard `ApiResponse<T>` envelope; `deviceId` is a raw GUID (no `dev_` prefix):

```json
{
  "devices": [
    {
      "deviceId": "8c1f5f64-5717-4562-b3fc-2c963f66afa6",
      "provider": "fitbit",
      "displayName": "Fitbit Charge 6",
      "status": "active",
      "isPrimary": true,
      "lastSyncedAt": "2026-08-07T08:30:00Z",
      "connectedAt": "2026-06-15T09:00:00Z",
      "tokenExpiresAt": "2026-08-07T09:30:00Z",
      "scopes": ["activity", "heartrate", "sleep"],
      "nextSyncAt": "2026-08-07T09:00:00Z",
      "todayUpdateCount": 4
    }
  ]
}
```

`scopes`, `nextSyncAt` and `todayUpdateCount` back the M1-15 device cards. All three are derived, not stored: scopes are parsed from the connection's scope JSON (a malformed value yields `[]` rather than an error), `nextSyncAt` is `lastSyncedAt + syncFrequencyMinutes` and is therefore an estimate rather than a scheduled job time, and `todayUpdateCount` counts today's activity records attributed to that connection. There is **no battery level** — no provider battery data is stored or fetched.

**Device Status Values** (mapped from the internal `ConnectionStatus` enum):

| Wire status | Internal status | Description |
|-------------|-----------------|-------------|
| `active` | `Connected` **and** `SyncError` | Connected; see quirk below |
| `disconnected` | `Disconnected` | OAuth connection removed |
| `token_expired` | `TokenExpired`, `AuthError` (and any other state) | OAuth token needs re-authorization |

> **Doc-noted quirk:** a device in `SyncError` (provider polling is failing) still reports `active` on the wire — a sync-failing device looks healthy to clients. There is **no `pending` status**: state before the callback completes lives only in the cache, never as a device row.

Errors: **403** if the JWT is valid but no local user row exists; **404** if the caller has no active link to the CardiMember.

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
| `redirectUri` | string | Yes | Deep link URI for mobile callback. Must be a `carditrack://` URI **with no fragment** — the bounce forwards into whatever is cached here and appends the callback params to it, so another scheme would be an open redirect and a `#` would swallow the params. Rejected at initiation rather than only at the bounce. (An "absolute URI" check alone is not enough: on Linux `Uri.TryCreate` accepts a bare path like `/oauth/callback` as an absolute `file:` URI.) |

### Response `200 OK`

```json
{
  "authorizationUrl": "https://accounts.google.com/o/oauth2/v2/auth?client_id=...",
  "state": "csrf_state_token_abc123",
  "codeVerifier": "pkce_verifier_xyz"
}
```

> The client stores `codeVerifier` and `state` locally, then redirects the user to `authorizationUrl`. After authorization the browser lands back on the app deep link (`redirectUri`) with `state` and either `code` or `error`; on `code` the app sends it to the OAuth callback endpoint, on `error` it surfaces the failure and stays put.

> **Provider redirect vs app deep link:** Google's web OAuth clients only accept **https** redirect URIs, so for providers with a configured `DeviceProviders[].RedirectUri` (Fitbit) the `redirect_uri` sent to the provider is the API's bounce endpoint below — not the deep link from the request body. The deep link is cached with the state and used by the bounce. Providers without a configured redirect keep the legacy direct-deep-link behavior.

---

## GET `/api/v1/oauth/redirect/{provider}`

Anonymous provider-facing redirect target (the "bounce"). Google redirects the wearer's browser here after consent; the endpoint looks up the pending `state` (without consuming it) and returns an **HTML hand-off page** that navigates the browser into the app deep link cached at initiation:

```
200 text/html   →  location.replace("carditrack://oauth/callback?state=...&code=...")
```

A `Location:` header naming a custom scheme is honoured by Chrome Custom Tabs and `ASWebAuthenticationSession` but dropped by browsers and proxies that only forward http(s), so the navigation is done from the page, with a tappable fallback link. Responses are `Cache-Control: no-store` and `Referrer-Policy: no-referrer` — the URL carries an authorization code.

**Every outcome hands off to the app**, because the deep link is the only thing that dismisses the in-app browser. When the provider returns no `code`, its `error`/`error_description` are forwarded (`error=invalid_request` when it names none):

```
carditrack://oauth/callback?state=...&error=access_denied&error_description=...
```

Only a `state` that cannot be resolved at all — absent, expired, already spent, or not this provider's — has nowhere to go; that renders a terminal "start the connection again" page with a `400`.

**Priority:** P0 | **Auth Required:** No (the state token scopes it; completing the flow still requires the authenticated callback below)

### Errors

| Code | Status | Description |
|------|--------|-------------|
| — | 400 | Missing `state`, or unknown/expired `state` for this provider (HTML, not JSON) |

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

Wrapped in `ApiResponse<T>`; full `DeviceResponse` shape (same as the list endpoint):

```json
{
  "deviceId": "8c1f5f64-5717-4562-b3fc-2c963f66afa6",
  "provider": "fitbit",
  "displayName": "Fitbit Charge 6",
  "status": "active",
  "isPrimary": true,
  "lastSyncedAt": null,
  "connectedAt": "2026-08-07T10:00:00Z",
  "tokenExpiresAt": "2026-08-07T11:00:00Z"
}
```

> **Upsert by device type:** if a connection for the same provider already exists on this CardiMember, the callback **updates it in place** (new tokens, status back to `Connected`) rather than creating a duplicate. A brand-new connection is marked `isPrimary` when it is the member's **first** device. This is also how **reconnect** works today — see below.

### Errors

No machine-readable `code` field is emitted — the `ErrorResponse` carries a human-readable `message`; branch on HTTP status:

| Status | When |
|--------|------|
| 400 | Invalid or expired state token (single-use, 15-min TTL, must match caller + provider); or unsupported/unconfigured provider |
| 403 | JWT valid but no local user row |
| 404 | Caller has no active link to the CardiMember bound to the state |
| 502 | Provider rejected the authorization code exchange |

> The planned `PROVIDER_PERMISSION_DENIED` (user denied scopes) case is **never produced** — a denial surfaces as a failed exchange (502) or the user simply never returns to the app.

---

## GET `/api/v1/cardimembers/{id}/devices/{deviceId}`

> **Planned — not yet implemented.** Use the list endpoint and filter client-side.

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

## POST `/api/v1/cardimembers/{id}/devices/{deviceId}/primary`

> **Implemented** (M1-15). Note the verb: `POST`, not the `PUT` originally planned. Previously `isPrimary` was set automatically — the member's first connection became primary and could never be changed.

Sets this device as the primary data source, clearing the flag from any previously primary device. Returns the updated device object (same shape as the list endpoint). **404** if the device does not belong to this CardiMember.

---

## POST `/api/v1/cardimembers/{id}/devices/{deviceId}/refresh`

> **Implemented** (M1-15) in place of the planned `/reconnect` endpoint below.

Renews the connection's OAuth token if it has expired and returns the updated device object. Takes **no request body**.

Deliberately **does not pull health data** — this endpoint is about the *connection*, not its contents. To pull on demand, use `POST .../devices/sync` below. When the provider cannot be reached, the connection is recorded as `token_expired` before the error is returned (**502**), so the stored state agrees with what the user was told.

A device whose OAuth grant has been revoked outright still needs the full reconnect flow below.

---

## POST `/api/v1/cardimembers/{id}/devices/sync`

> **Implemented** (issue #67). Backs the dashboard's refresh button, which previously only re-read what the Worker had already stored — so a member whose scheduled sync had not run yet sat on "Not synced yet" however often the caregiver tapped it.

Pulls **every active connection** the member has, now. Takes no request body and returns per-device outcomes:

```jsonc
{
  "syncedCount": 1,
  "failedCount": 1,
  "lastSyncedAt": "2026-08-08T12:00:00Z",
  "devices": [
    { "deviceId": "…", "provider": "fitbit", "succeeded": true,  "message": null },
    { "deviceId": "…", "provider": "garmin", "succeeded": false, "message": "We couldn't reach this device's provider." }
  ]
}
```

**200 even when some devices failed** — a member can have more than one connection, and one provider being down is not a failed request. `lastSyncedAt` is re-read from the connections afterwards rather than stamped from the clock, because a pull that dies mid-window deliberately leaves `LastSyncDate` where it was.

Refusals carry their own status: **409** when monitoring is paused (`MONITORING_PAUSED`) or the member has no connected device (`NO_CONNECTED_DEVICE`), and **429** when a manual sync ran for that member within the last minute (`SYNC_TOO_SOON`). The cooldown is per member and is claimed *before* any pull runs, so two caregivers refreshing at once cannot both spend the provider quota — which is per-app, not per-user.

Authorization is the **view** tier, not the management tier: refreshing surfaces nothing the caller could not already see, and a relative invited to watch over someone should not be staring at a dead refresh button.

**This is not a background job.** It runs inside the request that asked for it, and it reuses the same `IDeviceSyncService` per connection that `WearableSyncWorker` drives, so a manual pull and a scheduled one cannot diverge in what they store. *Scheduled* pulling and all DB polling remain `CardiTrack.Worker`'s alone, per `CLAUDE.md`.

> **Known limitation:** `DeviceSyncService` fetches a trailing window that **ends at yesterday**, because most providers only finalise a day's data after midnight. A manual sync therefore fills in history and clears "Not synced yet", but will not surface today's readings.

---

## POST `/api/v1/cardimembers/{id}/devices/{deviceId}/reconnect`

> **Planned — not yet implemented.** Reconnection works today by **re-running the normal connect + callback flow**: `POST .../devices` then `POST /api/v1/oauth/callback/{provider}`. The callback upserts by device type, so the existing connection gets fresh tokens and returns to `active` — no dedicated reconnect endpoint is needed for the happy path.

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
  "authorizationUrl": "https://accounts.google.com/o/oauth2/v2/auth?client_id=...",
  "state": "csrf_state_token_def456",
  "codeVerifier": "pkce_verifier_new"
}
```

> Follows the same PKCE OAuth flow as initial connection.

---

## DELETE `/api/v1/cardimembers/{id}/devices/{deviceId}`

> **Implemented** (M1-15). Requires a **primary-caregiver** link, not merely an active one.

Removes a device connection. Soft delete: the connection is deactivated, its status set to `disconnected`, and its **stored OAuth tokens discarded** — revoking the grant at the provider remains the user's own step. If the removed device was the primary, another active connection is promoted, so a member with devices always has a primary.

Historical data synced via this device is retained. A CardiMember **may have zero connected devices** (e.g. before their first connection); the dashboard reports `device.hasActiveConnection: false` in that state.

**Priority:** P1 | **Auth Required:** Yes

### Response `204 No Content`

### Errors

| Code | Status | Description |
|------|--------|-------------|
| `DEVICE_NOT_FOUND` | 404 | Device ID not found for this CardiMember |

---

**Supported Providers:**

| Provider | `provider` Value | Integration Mode | Status | Scopes / Permissions |
|----------|-----------------|------------------|--------|----------------------|
| Fitbit / Pixel Watch | `fitbit` | `server_oauth` | **Implemented** | Google Health API scope bundles: `activity_and_fitness.readonly`, `health_metrics_and_measurements.readonly`, `sleep.readonly` |
| Apple Health | `apple_health` | `on_device_bridge` | Planned | `HKQuantityTypeStepCount`, `HKQuantityTypeHeartRate`, `HKCategoryTypeAsleepCore` |
| Garmin | `garmin` | `server_oauth` | Config-only stub | `activities`, `heart_rate`, `sleep` |
| Samsung Health | `samsung_health` | `server_oauth` | Config-only stub | `steps`, `heart_rate`, `sleep` |
| Withings | `withings` | `server_oauth` | Config-only stub | `user.metrics` |

> **Config-only stubs:** `garmin`, `samsung_health`, and `withings` are accepted by request validation, but their configuration holds placeholder ClientIds and no provider client is registered in DI — a connect attempt fails with **400** ("not configured for connections"). Only Fitbit is wired end-to-end. See the [OAuth client inventory](../../../technical/oauth_clients.md) for provisioning state.

> The `fitbit` provider authorizes via **Google OAuth 2.0** and syncs through the **Google Health API** (`health.googleapis.com`), which covers Fitbit devices, Pixel Watch, and connected third-party sources — the legacy Fitbit Web API is decommissioned September 2026.

> **Integration modes:**
> - **`server_oauth`** — CardiTrack's backend holds OAuth tokens (AES-encrypted at rest) and **polls** the provider's cloud API on a 30-minute Worker cron — there is no webhook ingestion.
> - **`on_device_bridge`** (Apple Health — *planned*) — HealthKit has **no server-side OAuth**. Permissions would be granted on the CardiMember's iPhone and the mobile app would upload normalized samples; no ingestion endpoint for this exists yet, and `apple_health` is not a valid value for the OAuth endpoints above.

---

**Related:** [readme.md](readme.md) | [health-data.md](health-data.md) | [OAuth Client Inventory](../../../technical/oauth_clients.md) | [User Stories 1.3, 6.2](../../ui/mobile/user_stories.md)

**Last Updated:** August 7, 2026
