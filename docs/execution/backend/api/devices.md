# Device Management API

Handles wearable device connections via OAuth, device status management, primary device designation, and token refresh.

**Implementation status:** the core OAuth connection flow (list, connect, bounce redirect, callback) is **implemented**, as are the M1-15 management endpoints — **delete**, **set primary**, **refresh**, **suspend** and **resume** — and the connect flow's **add / reconnect / replace** modes (issue #1286: a member can have several devices, including several of one brand), plus an on-demand **sync** endpoint (issue #67) and the **wearer-side invitation** flow that lets the wearer authorize from their own device instead of the caregiver's phone. Get-single-device remains **planned — not yet implemented**; note the implemented routes differ from the planned shapes below (`POST .../primary` not `PUT`, `POST .../refresh` not `POST .../reconnect`).

Key implementation facts (verified against `DeviceConnectionService`):

- **Authorization is two-tier, member-link based** (failure → 404 "CardiMember not found" in both tiers, so an unauthorised caller can't tell a member exists). *Reading and connecting* — list, initiate, callback — need only an **active `UserCardiMember` link**. The *management* actions that change how a member is monitored — **delete, set-primary, refresh, suspend, resume**, and a connect or invitation in **`replace`** mode (which removes a device) — additionally require **`IsPrimaryCaregiver`**, so a relative invited only to watch over someone cannot cut off their data feed. **Sync** sits in the reading tier: it changes nothing about the connection and shows the caller nothing they could not already see. **Device invitations** (create, read, revoke) sit in the reading tier too, and deliberately: a caregiver who holds a link can already run the whole connection on their own phone, so requiring the stricter tier to do it by invitation would guard nothing while blocking the case the feature exists for. There are no Auth0 **role** checks on any device endpoint.
- **State tokens are single-use with a 15-minute TTL**, held server-side in the distributed cache keyed to the initiating user, member, and provider. The callback consumes the state even if the code exchange fails — a replayed state always fails.
- **Google authorize URLs include `access_type=offline`** (config-driven), without which Google issues no refresh token. `FirstConsentAuthorizationParams` (`prompt=consent select_account`) is added to every **add** and **replace**, and to a **reconnect** of a connection that holds no refresh token or whose grant has failed (`token_expired` — the stored token is the one that stopped working, and without consent Google sends no replacement). Adding a device may be for an account we have never held a token for, and the account chooser is how the caregiver picks which one — so another device's refresh token says nothing about this grant. Only a reconnect of a healthy connection that still banks a token skips it, because re-showing consent there reads as the connection having failed. A token exchange that returns no refresh token **leaves the stored one in place** rather than nulling it.
- **The provider account decides "same device or another one".** At completion the server reads the grant's account from the Google Health identity resource (`GET /v4/users/me/identity` → `healthUserId`) and stores it in `DeviceConnection.HealthUserId` immediately rather than on the first sync. Adding an account the member already has connected refreshes that connection instead of storing a second card over one data stream. That includes a `pixel_watch` on the same Google account as a `fitbit`, since both read through one API. The brand never decides it: two Fitbits on two accounts are two devices. See *Where a grant lands* under the callback.
- **OAuth tokens are AES-encrypted at rest** before being stored on the connection record.
- **Syncing is notify-then-fetch.** The `CardiTrack.HealthWebhookReceiver` Cloud Run service (`POST /webhooks/google-health`) receives the provider's data-availability notifications and publishes them to Pub/Sub; `NotificationDrainService` maps each notification's health-user id to the matching connections and runs a **targeted sync** through the same `IDeviceSyncService` the Worker uses. Because that stamps `LastSyncDate`, the routine poll's due-time moves out — making the Worker's 10-minute cron (`WearableSyncWorker`) the **fallback**, not a duplicate. The cron sets only how often the worker *looks*; a connection is actually due once its own `SyncFrequencyMinutes` (default 10) has elapsed. Connections belonging to a **removed or monitoring-paused** CardiMember — and **suspended** connections — are excluded by `GetDueForSyncAsync` (and by the webhook lookup, auth recovery and the sync audit, which share its gate), so a pause or suspension genuinely stops collection — see [cardimembers.md](cardimembers.md). Each due connection writes its own raw `DeviceActivityLogs` row, which is then merged into the member's single daily `ActivityLogs` row.
- The anonymous bounce endpoint **only redirects into the `carditrack://` app scheme** — any other cached redirect target is rejected, preventing open-redirect leakage of `code`+`state`. It now serves **two flows**, and which one a callback belongs to is carried by its state token and nothing else: an **app** state bounces into the deep link as before, a **wearer** state is completed server-side and renders a page. A state minted for one flow cannot be spent through the other's door — the two prove possession differently, and the check is explicit at both ends.
- **Only the GoogleHealth-backed providers (`fitbit`, `pixel_watch`) are actually connectable** — the GoogleHealth engine is the only one registered in DI. `garmin` and `withings` are the two dedicated integrations still to come; both have config blocks with **placeholder client ids**. **Apple Watch and Samsung Galaxy Watch will never get an engine of their own** (decided 2026-09-05 — see *Devices that arrive via Google Health* below): `samsung_health` still passes request validation but has no config block and fails like any other unconfigured provider. **Oura and Whoop were dropped from the roadmap** the same day; their config blocks and enum members are dead code awaiting cleanup. Every non-Google provider fails a connect attempt with 400 "not configured for connections".

### Real-time notifications

The webhook path is deliberately tolerant of the provider's payload shapes. `WebhookNotificationParser.ExtractHealthUserIds` accepts a `healthUserId` property (case-insensitive, at any nesting depth — the form live traffic uses) and the resource-name form `users/{id}[/dataTypes/…]` as a secondary format; extracted ids are trimmed and matched **exactly** against `DeviceConnection.HealthUserId`. A notification that yields no id is **acknowledged rather than retried**: the routine poll guarantees no data loss, so an unparseable notification costs at most ten minutes of latency, never a poison-message loop.

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
      "todayUpdateCount": 4,
      "batteryLevel": 72,
      "batteryStatus": "High",
      "historyRepull": {
        "repullId": "2e7a9d1c-3b44-4f0e-9a6b-1d2c3e4f5a6b",
        "status": "in_progress",
        "days": 30,
        "fromDate": "2026-08-10",
        "toDate": "2026-09-08",
        "daysDone": 14,
        "daysWithData": 11,
        "requestedAt": "2026-09-09T11:52:00Z",
        "startedAt": "2026-09-09T11:56:00Z",
        "completedAt": null,
        "nextAllowedAt": null
      }
    }
  ]
}
```

`historyRepull` is the connection's latest caregiver-requested history re-pull (see `POST .../devices/{deviceId}/history-repull` below), and is **usually null**: it is present while a request is open (`pending` / `in_progress`), while a `completed` one is still inside the re-pull cooldown — in which case `nextAllowedAt` says when the action is available again — and for **7 days** after a `failed` or `cancelled` one ended, so the caregiver learns the outcome; neither of those blocks re-requesting, so the card offers the action again alongside the notice. The server decides "still worth showing" so the rule can move without a mobile release.

`suspendedAt` is when a caregiver suspended the connection, or null while it collects (see `POST .../suspend`).

`scopes`, `nextSyncAt` and `todayUpdateCount` back the M1-15 device cards. All three are derived, not stored: scopes are parsed from the connection's scope JSON (a malformed value yields `[]` rather than an error), `nextSyncAt` is `lastSyncedAt + syncFrequencyMinutes` and is therefore an estimate rather than a scheduled job time, and `todayUpdateCount` counts today's activity records attributed to that connection.

`batteryLevel` (0–100) and `batteryStatus` (`High` | `Medium` | `Low` | `Empty`) are **both nullable and frequently absent**, and clients must render the tile only when one is present. They come from the Google Health API's `PairedDevice` resource (`GET /v4/users/me/pairedDevices`), captured on each sync and stored on the connection as a last-known value with no history behind it. They are null when:

- the connection never granted `googlehealth.settings.readonly` — the scope was added after the original three, so **every connection made before it reports no battery until the wearer reconnects**;
- the hardware carries no battery worth reporting (a scale);
- the last reading is older than 24 hours, in which case the server withholds it rather than present a stale percentage as current. Where several wearables are paired to one account, the **lowest** battery among them is the one reported — the point of the field is to warn that collection is about to stop.

**Device Status Values** (mapped from the internal `ConnectionStatus` enum):

| Wire status | Internal status | Description |
|-------------|-----------------|-------------|
| `active` | `Connected` **and** `SyncError` | Connected; see quirk below |
| `suspended` | any, while `SuspendedAt` is set | Suspended by a caregiver (M1-15). Takes precedence over the grant's own state, which returns once it is resumed; `nextSyncAt` is null |
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
  "redirectUri": "carditrack://oauth/callback",
  "mode": "replace",
  "deviceId": "8c1f5f64-5717-4562-b3fc-2c963f66afa6"
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `mode` | string | No | What the grant is for: `add` (default when omitted) adds a device alongside the member's others; `reconnect` re-authorises `deviceId` on the same account; `replace` connects a device in place of `deviceId` (M1-15 "Change Device"). `replace` needs a **primary-caregiver** link. See *Where a grant lands* under the callback. |
| `deviceId` | GUID | For `reconnect` / `replace` | The member's connection to act on. Must be absent for `add`. **404** if it is not one of this member's connections. A `reconnect` must name the connection's own brand (**400** otherwise) — a different brand is a `replace`. |
| `provider` | string | Yes | A **server-OAuth** provider: `fitbit`, `pixel_watch`, `garmin`, `withings`. `samsung_health` still passes validation but will never be configured — Galaxy Watch data arrives via Google Health (see below). `apple_health` is not a valid value and never will be. |
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

## Wearer-side invitations

Connecting a wearable used to require the wearer to be holding the caregiver's phone: the OAuth
round trip ran inside the app, so the consent screen could only appear there. That is fine for a son
setting up his mother's watch at her kitchen table and impossible for a daughter three hundred miles
away. These endpoints move the consent to the wearer's own device.

The caregiver mints an invitation and hands it over themselves — a share sheet, a QR code on screen.
**CardiTrack sends nothing and stores no address**: there is no email or phone field anywhere in this
flow, so we never learn who the link went to. The wearer opens it, sees who is asking and what would
be shared, and completes the provider's own consent from their own browser.

**What the invitation is.** A row in `DeviceConnectionInvites` holding a member id, the caregiver's
user id, a brand, a channel, the **SHA-256 of a 256-bit token**, and some timestamps. The token
itself is returned exactly once, in the create response, and is never stored, logged or re-issued —
a leaked database hands over no live invitations. There is no health data on the row, and no fact
about anyone that `UserCardiMembers` did not already hold.

**Lifetimes are per channel** (`DeviceInvites` configuration): a shared **link** lasts 24 hours
because it has to survive an unread inbox; a **QR code** lasts 15 minutes because both people are in
the room and nothing is waiting on a delivery. Expiry is stored as an instant, not a status, so an
invitation is expired the moment its deadline passes whether or not anything has swept it.

**At most one live invitation per member and brand.** Creating one supersedes any live predecessor —
asking for a new link is how a caregiver takes back one they sent to the wrong person. The rule is a
partial unique index, not just a service check, so two caregivers tapping at once cannot both win.

**Statuses:** `pending` → `opened` → one of `completed`, `declined`, `revoked`; plus `expired`,
which the API reports as a status even though it is stored as a deadline, so the caregiver's waiting
screen is not re-deriving it from a device clock that may disagree with ours.

Finished and expired invitations are deleted by `RetentionWorker` after `DeviceInvites:RetentionDays`
(30). The durable record of the same events is the audit trail, which keeps them longer.

---

## POST `/api/v1/cardimembers/{id}/device-invites`

Mints an invitation and returns the one-time URL to hand the wearer.

**Priority:** P1 | **Auth Required:** Yes (active `UserCardiMember` link)

### Request Body

```json
{
  "provider": "fitbit",
  "channel": "qr",
  "replacesDeviceId": null
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `provider` | string | Yes | `fitbit`, `pixel_watch`, `garmin`, `samsung_health`, `withings` |
| `channel` | string | Yes | `link` (24 h) or `qr` (15 min) — sets the lifetime |
| `replacesDeviceId` | GUID | No | The connection the wearer's device replaces (M1-15 "Change Device"). Null adds a device alongside the others. Checked **at creation**, not when the wearer returns: it needs a **primary-caregiver** link and one of this member's connections (**404** otherwise). The old connection is removed in the same transaction that stores the new one, so a wearer who never finishes leaves it untouched. Echoed back as `replacesDeviceId` on every read. |

### Response `201 Created`

```json
{
  "success": true,
  "message": "Here's the link to send them.",
  "data": {
    "inviteId": "8f2c…",
    "provider": "fitbit",
    "channel": "qr",
    "status": "pending",
    "url": "https://api.carditrack.com/connect?t=…",
    "expiresAt": "2026-09-18T09:15:00Z",
    "openedAt": null,
    "resolvedAt": null,
    "deviceId": null,
    "replacesDeviceId": null
  }
}
```

`url` is populated **only here**. Every later read leaves it null: it is a live credential, and a
status endpoint that kept re-issuing it would turn the waiting screen's poll into a repeated chance
to leak one.

### Errors

| Code | Status | Description |
|------|--------|-------------|
| `VALIDATION_ERROR` | 400 | Unknown `provider`, or `channel` that is neither `link` nor `qr` |
| `UNSUPPORTED_PROVIDER` | 400 | Provider has no configured client |
| — | 404 | No active link to this CardiMember |

---

## GET `/api/v1/cardimembers/{id}/device-invites/{inviteId}`

One invitation's current state — what the caregiver's waiting screen polls. Same body as above with
`url` null. 404 when the invitation belongs to a different member, so an id guessed off one member
cannot read another's.

**Priority:** P1 | **Auth Required:** Yes

---

## DELETE `/api/v1/cardimembers/{id}/device-invites/{inviteId}`

Cancels an invitation. Returns `200` with the invitation's **resulting** state, which is `completed`
rather than `revoked` when the wearer got there first — a cancel that lost a race has still left the
caregiver where they wanted to be, and reporting a failure would send them looking for a problem
that is not there.

**Priority:** P1 | **Auth Required:** Yes

---

## `GET /connect`, `POST /connect/start`, `POST /connect/decline`

The wearer's three pages. **Anonymous**, HTML, and outside `/api/v1` — the person they serve has no
CardiTrack account and, by the product decision of 2026-08-10, never will.

| Route | Does |
|-------|------|
| `GET /connect?t=…` | The consent ask: who is asking, about whom, which brand, what would be shared, and a privacy link |
| `POST /connect/start` | Marks the invitation opened, mints wearer-channel PKCE state, `302`s to the provider's consent screen |
| `POST /connect/decline` | Ends the invitation as `declined` and says so |

**The token is the whole authorization**, backed by a per-IP rate limit on `/connect*`. What holding
one gets you is bounded by the service, not by the controller: every method takes a token and
nothing else — no member id, user id or provider for a caller to substitute — and the most any page
discloses is **two first names, a brand and a deadline**. No surname, no email, no date of birth, no
reading, no alert, no other member of the family.

**Every unusable link gets byte-for-byte the same page and the same `404`** — unknown, expired,
already used, declined and revoked alike. Distinguishing them, in the copy or in the status, would
answer for any token somebody cared to try whether it had ever been a real invitation.

**The two actions are POSTs, not links.** A `GET` would let any preview fetch — a messaging app
unfurling the link, a mail scanner — start or end the flow before the wearer had read a word of it.
They carry no anti-forgery token and need none: a forged cross-site post would have to carry the
invitation token in its body, and anyone holding that can call these endpoints directly. There is no
ambient credential here for a forgery to ride on.

**The token travels in the query string**, which is normally where a credential should not go. A QR
code and a shared link have nowhere else to put it; request logging strips query strings entirely,
the audit trail records paths only, and every page sends `Referrer-Policy: no-referrer`. Pages also
send `Cache-Control: no-store`, `X-Frame-Options: DENY` and a content security policy of
`default-src 'none'` with `form-action 'self'` — they carry **no script at all**, so a content
injection would have nothing to execute.

**Audit.** These requests have no authenticated subject, so `AuditLoggingMiddleware` would record
nothing; the invite service writes the entries itself (`CreateDeviceInvite`, `OpenDeviceInvite`,
`CompleteDeviceInvite`, `DeclineDeviceInvite`, `RevokeDeviceInvite`) against the **caregiver**, who
is the accountable party in every case.

---

## GET `/api/v1/oauth/redirect/{provider}`

Anonymous provider-facing redirect target (the "bounce"). Google redirects the wearer's browser here after consent; the endpoint looks up the pending `state` (without consuming it) and returns an **HTML hand-off page** that navigates the browser into the app deep link cached at initiation:

```
200 text/html   →  location.replace("carditrack://oauth/callback?state=...&code=...")
                 →  Android: intent://…#Intent;scheme=carditrack;package=…;end, then window.close()
```

A `Location:` header naming a custom scheme is honoured by Chrome Custom Tabs and `ASWebAuthenticationSession` but dropped by browsers and proxies that only forward http(s), so the navigation is done from the page, with a tappable fallback link. After the deep link fires the page calls `window.close()` (and on Android prefers Chrome's `intent://` form that names the app package) so the tab does not stay in the task for a later "Go to Dashboard" to walk back into. Responses are `Cache-Control: no-store` and `Referrer-Policy: no-referrer` — the URL carries an authorization code.

**Every outcome hands off to the app**, because the deep link is the only thing that dismisses the in-app browser. When the provider returns no `code`, its `error`/`error_description` are forwarded (`error=invalid_request` when it names none):

```
carditrack://oauth/callback?state=...&error=access_denied&error_description=...
```

Only a `state` that cannot be resolved at all — absent, expired, already spent, or not this provider's — has nowhere to go; that renders a terminal "start the connection again" page with a `400`.

### Two flows, one redirect URI

The provider's registered redirect URI is one fixed route per API, so this endpoint serves the
wearer-side flow as well. **The state token is the only thing that says which** — the provider sends
back exactly what we sent it, so the state is the only part of the request we minted.

A **wearer** state is not bounced anywhere: there is no app to return to. The endpoint exchanges the
code itself, stores the connection under the member and caregiver the invitation named, closes the
invitation out, and renders a page ("All set — thank you", or "Nothing was shared"). A wearer's
browser is never shown a `carditrack://` link or an `intent://` URL — the first would be a dead tap
and the second would offer to install an app they never asked for.

Two things are checked at completion that the app flow has no need of. The **caregiver's access is
re-read**, because an invitation can outlive the authority that issued it and this is the moment
health data would start flowing to somebody already cut off. And the **channel must match**: a state
minted for the app cannot be completed through the wearer path, nor the reverse. The two flows prove
possession differently — the app by a verifier held on the phone, the wearer by a verifier we never
released — and the wearer state carries the caregiver's own user id, so without an explicit channel
check a caregiver could post their own invitation's state to the authenticated callback and complete
a grant the wearer had started but never finished giving.

A refusal at the provider (`error=access_denied`) **leaves the invitation live**, so the wearer can
change their mind from the page they land on rather than going back to the caregiver for a fresh
link over a mis-tap.

**Priority:** P0 | **Auth Required:** No (the state token scopes it; completing the *app* flow still requires the authenticated callback below)

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

Two fields exist for this response only: `alreadyConnected` (true when an `add` turned out to be an account the member already had, so that connection was refreshed) and `replacedDeviceId` (the connection a `replace` removed; null otherwise). The success `message` says which happened.

### Where a grant lands

The state token carries the initiation's `mode` and `deviceId`; the grant's provider account (see *Key implementation facts*) is compared with the member's connections on the same API:

| Mode | Account is… | Result |
|------|-------------|--------|
| `add` | one the member already has connected | That connection is refreshed; `alreadyConnected: true` |
| `add` | new, or could not be read | A **new connection**. Primary only if the member has no primary |
| `reconnect` | the connection's own, or could not be compared | That connection gets fresh tokens and returns to `active`. If the account could not be read, its stored `HealthUserId` is cleared rather than kept, so the next sync captures the right one. When Google also sends no new refresh token, the stored one is kept **only if the account positively matches**. Otherwise it is dropped: the new access token may be another account's, and the old refresh token would switch the card back at the next expiry. The connection then asks for a reconnect when the access token expires |
| `reconnect` | one another of the member's connections holds | **409** (`ACCOUNT_ALREADY_CONNECTED`), as for a replacement. It matters when the reconnected device's own account was never captured and so cannot be compared |
| `reconnect` | a different one | **409**. Nothing is stored — switching the account under an existing card would show a stranger's data under this member. The app offers "Change device" instead |
| `replace` | the replaced connection's own | Just a reconnect of it; `replacedDeviceId: null` |
| `replace` | another of the member's connections' | **409**. Two cards would read one data stream |
| `replace` | new, or could not be read | A new connection that **takes over the replaced one's primary flag**, and the replaced connection is removed (soft-deleted, tokens discarded) **in the same save**. Its grant is queued for revocation in that same save. The Worker ends it only if, by then, no live connection may share it (same rule as `DELETE`, below). While the new device's account is unknown, the new device itself counts as possibly sharing the grant: revoking a Google refresh token ends the whole grant, so doing it when unsure could take the new connection down with it |

**A refused grant is queued for revocation.** When completion is refused after the code exchange, a grant is live at Google that nothing here will ever hold. That covers a reconnect on another account, a replacement onto an account already held, an invitation withdrawn mid-consent, and a request cancelled part-way. Every failure after the exchange goes through this path, including the identity lookup and opening the transaction. An account that could not be read is never queued. A provider timeout during the identity lookup counts as an unknown account. Queuing is best-effort and never replaces the refusal the caller is told.

**Revocation is never made in the request; it is queued.** `PendingGrantRevocations` gets a row in the same transaction that discards the tokens: a removed device, a replaced one, a removed member's devices, or a refused grant. The row holds the encrypted token, the member, the brand and the account. `GrantRevocationWorker` (every minute) drains it. For each row it:
1. re-checks the shared-grant rule (under `DELETE`, below), and drops the row without revoking if the grant may be shared;
2. otherwise calls the provider;
3. on a failure or timeout, retries on a widening backoff (5 min, tripling, capped at a day). After 8 attempts, about three days, it drops the row and logs an error naming the connection. The token must not sit in the database indefinitely.

So a provider timeout, a cancelled request or a crash after the commit can no longer lose a revocation. A remove or replacement is reported as done as soon as it commits. Member erasure ends a member's queued grants itself before deleting them with the rest of the member. A provider timeout while it revokes is reported as an unrevoked grant; it does not abort the erasure. The provider calls are made before erasure takes its locks, so a slow provider never holds them. Under the locks it reads the grants again and revokes only one stored in between by a device change, one whose token a reconnect replaced, or one it had kept as shared that another member no longer reads through. It keeps any grant — live or queued — whose account another member's live connection still reads through, since revoking a Google refresh token ends the grant for the whole account and that member is not being erased.

An account **matches** a connection when one identifier agrees and neither is known to disagree. A row can carry one stale identifier beside a current one.

**Changes to a member's devices are serialized.** The rules over a member's devices each span the whole set: one primary, one connection per account, never the last collecting device suspended. Each is a read followed by a write, so two changes interleaving could each pass and together break the rule. Examples: two replacements both promoting their new device, or two suspensions each seeing the other device still collecting. Every change therefore runs in a transaction that holds a per-member advisory lock (`pg_advisory_xact_lock` on a `device-connections:{memberId}` key) before it reads. That covers a callback storing a grant, delete, set-primary, suspend and resume. Member removal (`DELETE /cardimembers/{id}`) takes it too, and reads the devices it queues for revocation only after it holds the lock. A connect that was waiting then sees the member inactive and refuses, and never stores a grant that removal missed. A lock rather than `FOR UPDATE` on the member row, which the member write guard and erasure already coordinate on. Erasure takes the same device lock *before* its `FOR UPDATE` on the member row, and before it reads the member's grants. That is the order every device change takes them in: the device lock, then the member row, which a removal updates and a connect's insert key-share locks through the foreign key. The opposite order would deadlock erasure against a removal or connect. A device change holding the lock is waited for, so a connect cannot store a connection erasure never saw. A change that was waiting for the lock re-checks under it that the member is still live, and refuses with 404 when erasure won. A callback refused that way queues the grant it had just been issued, as any failure after the code exchange does.

A state cached before modes existed deserializes as `add`. With the account match, an older app build's reconnect on the same account still lands on the connection it came from.

`pixel_watch` and `fitbit` share one API. The same Google account connected as both is therefore one account (`alreadyConnected`), not two devices.

### Errors

No machine-readable `code` field is emitted — the `ErrorResponse` carries a human-readable `message`; branch on HTTP status:

| Status | When |
|--------|------|
| 400 | Invalid or expired state token (single-use, 15-min TTL, must match caller + provider); or unsupported/unconfigured provider |
| 403 | JWT valid but no local user row |
| 404 | Caller has no active link to the CardiMember bound to the state; or the `deviceId` a reconnect/replace named is no longer one of its connections; or a `replace` whose caller is no longer a primary caregiver |
| 409 | A `reconnect` came back on a **different account** (`DIFFERENT_ACCOUNT`), or a `reconnect` or `replace` came back on an account **another of the member's devices** already holds (`ACCOUNT_ALREADY_CONNECTED`) |
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

Sets this device as the primary data source, clearing the flag from any previously primary device. Returns the updated device object (same shape as the list endpoint). **404** if the device does not belong to this CardiMember; **409** if it is suspended — resume it first.

---

## POST `/api/v1/cardimembers/{id}/devices/{deviceId}/suspend`

> **Implemented** (M1-15, issue #1286). Requires a **primary-caregiver** link. No request body. Idempotent.

Stops the device collecting while keeping its tokens and its history. It is skipped by:
- scheduled and manual syncs;
- webhook-triggered pulls;
- auth recovery and the sync audit;
- history re-pulls (a queued one is cancelled as not syncable);
- the inactivity probe;
- the device nudges.

Stored as `DeviceConnection.SuspendedAt` / `SuspendedByUserId`, **beside** `ConnectionStatus` rather than as another value of it. The sync and auth-recovery paths write that status as they learn about the grant. A suspension stored there would be overwritten, or would hide that the grant expired while suspended. Those writers also cannot overwrite the suspension itself. `DeviceConnectionRepository.Update` writes only the columns a unit of work changed, and never a whole entity that was read before the save. A token refresh runs its provider call outside the member's device lock, so it may have read the device before a suspension committed. Its save writes the new tokens and leaves `SuspendedAt` as the suspension set it.

**Open-ended**, unlike Pause Monitoring: the member's other devices go on collecting. For the same reason it is **refused with 409 (`LAST_ACTIVE_DEVICE`)** when no *other* device is collecting — that is, unsuspended with its grant `active`. A device waiting on a reconnect does not count. Stopping a member's only data feed is what Pause Monitoring is for, and that is bounded (1 hour to 7 days).

The queries that select devices for collection leave suspended ones out. The sync (routine, webhook-triggered and audit), auth recovery and environmental enrichment also re-check `SuspendedAt` when they run, so a batch selected just before the suspension committed does not still pull. Only a pull already in flight at that moment completes.

If the suspended device was primary, the flag moves to another collecting device. A member whose devices are **all** suspended gets no device-silence alert: collection stopped because a caregiver stopped it. That is reachable by removing the last collecting device while the rest are suspended.

Returns the device with `status: "suspended"`.

## POST `/api/v1/cardimembers/{id}/devices/{deviceId}/resume`

> **Implemented** (M1-15, issue #1286). Requires a **primary-caregiver** link. No request body. Idempotent.

Clears the suspension. The sync worker picks the device up on its next pass, since its last sync is long past due. The device takes the primary flag back **only if no other device holds it**: resuming is not a vote to make it primary.

**Known behaviour:** the first pull after resuming runs the normal repair lookback (`SyncLookbackDays`, 3 days on Google Health). Up to that many days recorded on the device while it was suspended are therefore collected.

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

Refusals carry their own status: **409** when monitoring is paused (`MONITORING_PAUSED`) or the member has no connected device (`NO_CONNECTED_DEVICE`), and **429** when a manual sync ran for that member within the last minute (`SYNC_TOO_SOON`). The cooldown is per member and is claimed before any pull runs.

It is a **rate limiter, not a mutex**: `IDistributedCache` has no set-if-absent, so the claim is a get-then-set and two requests arriving in the same instant can both pass. It stops the case that actually occurs — a caregiver tapping refresh repeatedly, which is sequential — and losing the race costs one extra pull against a quota measured in hundreds per hour. A Redis `SET NX` claim would only hold where Redis is configured (the cache falls back to in-memory), so it is not worth the second code path today.

Authorization is the **view** tier, not the management tier: refreshing surfaces nothing the caller could not already see, and a relative invited to watch over someone should not be staring at a dead refresh button.

**This is not a background job.** It runs inside the request that asked for it, and it reuses the same `IDeviceSyncService` per connection that `WearableSyncWorker` drives, so a manual pull and a scheduled one cannot diverge in what they store. *Scheduled* pulling and all DB polling remain `CardiTrack.Worker`'s alone, per `CLAUDE.md`.

`DeviceSyncService` fetches a trailing window that **ends at today** and reaches back `SyncLookbackDays` complete days, so a manual sync both surfaces today's readings and repairs the days a provider has since revised. A manual sync never extends history further back and never fetches the granular (minute-grain) series — both belong to the Worker's cadence (`SyncScope.WorkerCadence`), so a caregiver's refresh never waits on last month or five extra series. To reach further back a caregiver asks for a **history re-pull** (next section), which the Worker runs in the background. Today's figures are partial by nature: the dashboard reports steps for a day in progress against the member's goal rather than against their whole-day average, since a part-finished day compared with a full one reads as a collapse every morning.

---

## POST `/api/v1/cardimembers/{id}/devices/{deviceId}/history-repull`

> **Implemented** (M1-15 "Re-pull History"). Fills gaps the routine sync left — days the Worker missed while it was down, or that the provider revised after the trailing window had moved on — by re-reading a caregiver-chosen stretch of one connection's history from its provider.

Request body:

```json
{ "days": 30 }
```

`days` is 1–90 and counts **complete** days back from yesterday: today is the routine sync's, pulled every ten minutes already. The mobile app offers 7 / 14 / 30 / 45 / 60 / 75 / 90; the API accepts any value in range.

### Response `202 Accepted`

```json
{
  "repullId": "2e7a9d1c-3b44-4f0e-9a6b-1d2c3e4f5a6b",
  "status": "pending",
  "days": 30,
  "fromDate": "2026-08-10",
  "toDate": "2026-09-08",
  "daysDone": 0,
  "daysWithData": 0,
  "requestedAt": "2026-09-09T11:52:00Z",
  "startedAt": null,
  "completedAt": null,
  "nextAllowedAt": null
}
```

**202, not 200** — the request is recorded, not executed. Up to 90 days at up to 26 provider calls a day (before pagination) is far too much to run inside a request, and *scheduled pulling belongs to `CardiTrack.Worker` alone* per `CLAUDE.md`, so this endpoint writes a `DeviceHistoryRepull` row and returns; `HistoryRepullWorker` drains it. Progress arrives on `GET .../devices` as `historyRepull` — poll that (the app reloads the list after the tap, on pull-to-refresh and on resume), there is no per-request endpoint.

**What the Worker does with it.** Every ten minutes (offset from the routine pull's minute, so a wearer never pays for both in the same sixty seconds) it advances up to `MaxPerTick` (5) open requests by one chunk of `BackfillChunkDays` (7) days each, **newest first**, so the days a caregiver is looking at land first. Each day fetches the daily snapshot *and* the granular (minute-grain) series — a re-pull is "everything the provider has for these days" — and stores them through the same upsert-and-merge path as every other pull, so a re-pull **fills and refreshes days but never deletes one**: a day the provider has nothing for is left exactly as it was, and is not stored as an empty row. `daysDone` is the oldest day reached counting from `toDate`; a 30-day request is 5 chunks (~50 minutes), a 90-day one is 13 (~2 hours). A chunk cut short by a provider failure counts an attempt and is retried next tick; after three failed attempts the request is `failed`, and everything fetched before that stays. A request is `cancelled` rather than failed when monitoring is paused or the connection stops being syncable after it was queued. Neither outcome moves the connection to `SyncError` — a day the provider refuses two months back says nothing about whether the device works today; the routine sync is what decides that.

Refusals carry their own status. **409** when monitoring is paused (`MONITORING_PAUSED`), the connection is removed / disconnected / waiting on a refused token (`DEVICE_NOT_SYNCABLE` — a `SyncError` connection is still accepted, last time's hiccup being exactly the gap a re-pull fills), or a re-pull for this connection is already open (`REPULL_IN_PROGRESS`); **429** when one *completed* within the cooldown (`REPULL_TOO_SOON`). The cooldown is `HistoryRepullCooldownHours` per provider block — **48 hours** in every environment (`history_repull_cooldown_hours` in tfvars), 0 disables it — and is what bounds the quota a caregiver can spend: 90 days is up to 2,340 requests at one page per series (up to 26 a day: the daily snapshot and the five granular series), more for a high-cadence wearer whose series span several pages, against the wearer's per-user ceiling. Failed and cancelled requests do not start a cooldown. One-open-per-connection is enforced by a partial unique index, not just the pre-insert check, so two caregivers tapping at once get one request and one `REPULL_IN_PROGRESS`. The route also carries an IP rate-limit rule of 10 per hour.

Authorization is the **view** tier, like the manual sync: a relative invited to watch over someone should be able to fill a gap they noticed, and the cooldown — not the tier — is the guard on quota. Denial is 404. The class-level audit attribute records every request against the member.

---

## POST `/api/v1/cardimembers/{id}/devices/{deviceId}/reconnect`

> **Planned — not yet implemented, and no longer needed for the happy path.** Reconnection re-runs the normal connect + callback flow with **`mode: "reconnect"`** and the connection's `deviceId`: `POST .../devices` then `POST /api/v1/oauth/callback/{provider}`. The existing connection gets fresh tokens and returns to `active`. A grant on a different account is refused (409) rather than switched in place.

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

Removes a device connection. Soft delete: the connection is deactivated, its status set to `disconnected`, and its **stored OAuth tokens discarded, and the grant queued for revocation at the provider** in the same transaction. The Worker ends the grant within about a minute, retrying on failure (see *Revocation is never made in the request* above). CardiTrack then stops appearing in the wearer's list of apps with access to their health data, and a leaked copy of the refresh token can no longer be exchanged for readings. A provider outage never stops a caregiver disconnecting a device.

**Revocation is skipped whenever the grant may be shared.** Revoking a Google refresh token ends the grant for the whole account, so any other connection reading through it would be cut off too. It counts as shared when either:
- another of the member's live connections on the same API is **not known to be on a different account** — both identities captured and different. Identity capture is best-effort, and an uncaptured one may well be the same account; or
- any member's live connection has the same `HealthUserId`.

The removed connection's tokens are discarded either way. The check runs in the Worker immediately before the provider call. A grant for the same account stored since the device was removed, on any member, therefore still stops the revocation.

One case no database check can close: a grant whose code exchange with Google has already happened but whose row is not yet stored. Revocation is ordered against Google's token issuance, which happens before we know the account. That connection fails its next sync and reads `token_expired`. The caregiver is asked to reconnect; nothing is read under the wrong member. The Worker's re-check has the same edge. It reads the member's devices just before the provider call without holding the member's device lock, so a device whose exchange already happened can be stored in that window, and holding the lock would not help: its grant was issued before our database could see it.

If the removed device was the primary, another connection is promoted: a collecting one by preference, then an unsuspended one, then a suspended one. A member with devices therefore always has a primary. The last case arises when the only collecting device is removed while the rest are suspended.

Historical data synced via this device is retained. A CardiMember **may have zero connected devices** (e.g. before their first connection); the dashboard reports `device.hasActiveConnection: false` in that state.

**Priority:** P1 | **Auth Required:** Yes

### Response `204 No Content`

### Errors

| Code | Status | Description |
|------|--------|-------------|
| `DEVICE_NOT_FOUND` | 404 | Device ID not found for this CardiMember |

---

**Supported Providers:**

| Brand (`DeviceType`) | `provider` Value | Data-source API (`HealthApi`) | Integration Mode | Status | Scopes / Permissions |
|----------------------|-----------------|-------------------------------|------------------|--------|----------------------|
| Fitbit (`Fitbit`) | `fitbit` | `GoogleHealth` | `server_oauth` | **Implemented** | Google Health API scope bundles: `activity_and_fitness.readonly`, `health_metrics_and_measurements.readonly`, `sleep.readonly`, `settings.readonly` (battery; added later, so pre-existing connections lack it) |
| Google Pixel Watch (`GooglePixelWatch`) | `pixel_watch` | `GoogleHealth` | `server_oauth` | **Implemented** (same engine as `fitbit`) | Same Google Health API bundles as `fitbit` |
| Garmin (`Garmin`) | `garmin` | `GarminConnect` | `server_oauth` | Config-only stub (placeholder client id) — R2. **Developer-program access unconfirmed**, see caveat below | `activities`, `heart_rate`, `sleep` |
| Withings (`Withings`) | `withings` | `Withings` | `server_oauth` | Config-only stub (placeholder client id) — R4. Self-serve OAuth 2.0 API with webhooks; the fallback next engine if Garmin stays closed | `user.metrics` |
| Apple Watch (`AppleWatch`) | — (not yet mapped) | `GoogleHealth`, via the wearer's Google Health app | `via_google_health` | **No dedicated integration — decided 2026-09-05.** Follow-up: add `AppleWatch` to the GoogleHealth block's `DeviceTypes` and a `provider` string so the picker can record the brand and land in the Google connect flow; then a live-device check of which metrics Apple Health passes through | Same Google Health API bundles as `fitbit` |
| Samsung Galaxy Watch (`GalaxyWatch`) | `samsung_health` (accepted, never configured) | `GoogleHealth`, via Health Connect → the wearer's Google Health app | `via_google_health` | **No dedicated integration — decided 2026-09-05.** Same follow-up as Apple Watch. `HealthApi.SamsungHealth` and the seeded `api.shealth.samsung.com` endpoint describe an API that does not exist for third parties | Same Google Health API bundles as `fitbit` |
| Oura (`Oura`) | — | — | — | **Dropped 2026-09-05.** Enum member, `HealthApi.Oura` and the appsettings block are dead code awaiting cleanup | — |
| Whoop (`Whoop`) | — | — | — | **Dropped 2026-09-05.** Same leftovers as Oura | — |

> **Brand vs API:** a `provider` value names the **hardware brand** the wearer picked; which data-source API it connects through is the `DeviceProviders` configuration's `DeviceTypes` mapping (e.g. the `GoogleHealth` block lists `["Fitbit", "GooglePixelWatch"]`). Brands on the same API share one OAuth client, one engine, and one registered bounce redirect — a `pixel_watch` authorization returns through the `/oauth/redirect/fitbit` segment, and the callback validates state at the **API level** while the connection keeps the brand from initiation.

> **Stubs:** `garmin`, `samsung_health`, and `withings` are accepted by request validation, but no provider block claims their DeviceTypes with a real client (and no engine is registered in DI) — a connect attempt fails with **400** ("not configured for connections"). `garmin` and `withings` have config blocks with placeholder client ids and are the only two dedicated integrations still planned. `samsung_health` has no config block and never will — see *Devices that arrive via Google Health*. **Oura and Whoop** still have config blocks and enum members but no `provider` string maps to them; both were dropped from the roadmap on 2026-09-05, so what remains is cleanup, not work in progress. Only the GoogleHealth engine is wired end-to-end. See the [OAuth client inventory](../../../technical/oauth_clients.md) for provisioning state.
>
> **Garmin access caveat (2026-09-05):** Garmin's Connect Developer Program is business-only and approval-gated (OAuth 2.0 + PKCE, push webhooks, no fee). Garmin's own FAQ still advertises a two-business-day review, but several mid-2026 developer reports say the public request form was withdrawn with no ETA. Apply before scheduling the R2 engine work; if access does not come, Withings is the next engine.

> The `fitbit` and `pixel_watch` providers authorize via **Google OAuth 2.0** and sync through the **Google Health API** (`health.googleapis.com`), which covers Fitbit devices, Pixel Watch, and connected third-party sources — the legacy Fitbit Web API is decommissioned September 2026.

> **Integration modes:**
> - **`server_oauth`** — CardiTrack's backend holds OAuth tokens (AES-encrypted at rest) and pulls from the provider's cloud API **notify-then-fetch**: the Google Health webhook triggers a targeted sync, and the 10-minute Worker cron is the fallback poll (see "Real-time notifications" above).
> - **`via_google_health`** (Apple Watch, Samsung Galaxy Watch) — no CardiTrack code at all beyond the GoogleHealth engine. The wearer shares the watch into the **Google Health app** on their own phone (Apple Health on iPhone, Health Connect on Android); Google holds the readings in the wearer's Google account, and the Google Health API serves them to CardiTrack exactly as it serves Fitbit data. The former `on_device_bridge` idea (HealthKit in the MAUI app plus an upload endpoint) is **retired** — see below.

### Devices that arrive via Google Health

**Decision (2026-09-05):** CardiTrack will not build an integration of its own for Apple Watch or Samsung Galaxy Watch. Neither vendor offers a server-side data connection of the kind Google, Garmin and Withings do — Apple's HealthKit and Samsung's Health Data SDK both run only on the wearer's phone, which would mean the wearer installing CardiTrack, contradicting the product rule that the wearer is never an app user. Google's Health app accepts readings from both (and from Garmin, Withings, Xiaomi, Amazfit, Oura and Whoop) through Apple Health and Health Connect and stores them in the wearer's Google account, which the Google Health API already exposes to us.

What that means in practice:

- **Engine work: none.** The existing GoogleHealth engine, OAuth client, webhook subscription and quota model cover it.
- **Follow-up (small):** add `AppleWatch` and `GalaxyWatch` to the GoogleHealth provider block's `DeviceTypes`, give each a `provider` string, and have the mobile picker route those brands to the Google connect flow — so the connection records the brand the wearer actually has while the API stays Google. `HealthApi.AppleHealth` and `HealthApi.SamsungHealth` then have no purpose; the `Device` seed row that gives Samsung an `api.shealth.samsung.com` endpoint is informational only (the column is never read) and can be corrected in the same cleanup.
- **Open verification:** Google states that third-party metric coverage "depends on what the third-party app or device chooses to share". Nobody has yet run an Apple Watch or a Galaxy Watch through this route against the field-population probe. Heart rate, steps and sleep are the ones the alert rules rely on; confirm those first with the first beta wearer who has one.
- **What the wearer does:** install the Google Health app, allow it to read from Apple Health or Health Connect, then approve CardiTrack once on Google's consent screen. The public walkthrough lives at carditrack.com/supported-devices.

### Legacy Fitbit Web API — not exposed

Fitbit's own developer interface (`api.fitbit.com`, the Fitbit Web API) shuts down permanently on **2026-09-30**, and Google's migration guide notes that legacy Fitbit OAuth tokens do not carry over. **CardiTrack has never called it.** Verified 2026-09-05:

- The only `api.fitbit.com` string in the repository is the informational `ApiEndpoint` on the seeded Fitbit `Device` row in the original 2026-03 `InitialCreate` migration, rewritten to `health.googleapis.com` by the 2026-08-13 `AddGooglePixelWatchDevice` migration. `Device.ApiEndpoint` is never read at runtime.
- Every `appsettings.json` (API, Worker, PipelineJobs) and both tfvars point `ApiBaseUrl` at `health.googleapis.com` and `AuthorizationUrl` at `accounts.google.com`.
- Every stored `DeviceConnection` token was issued by Google OAuth against the `carditrack-devices-{env}` client (registered 2026-08-07). There is no legacy-token population and no forced re-consent to run.

The 2026-09-30 date therefore changes nothing for CardiTrack. The live-wearer field-population check ([oauth_clients.md](../../../technical/oauth_clients.md) step 5b) stays open on its own merits, not because of the sunset.

---

**Related:** [readme.md](readme.md) | [health-data.md](health-data.md) | [OAuth Client Inventory](../../../technical/oauth_clients.md) | [User Stories 1.3, 6.2](../../ui/mobile/user_stories.md)

**Last Updated:** September 5, 2026
