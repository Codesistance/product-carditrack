# Notifications API

> **Status: Fully implemented.** The **in-app data-completeness inbox** (see "Implemented today") and the **push delivery spine** — device tokens, FCM/APNs relay, the delivery outbox, acknowledgement and escalation — both ship. See [notification_engine.md](../../../technical/notification_engine.md) §17 for the small set of Phase 3 items deliberately deferred (the iOS notification service extension's Xcode project itself, the active daily liveness probe).

Two things share this path prefix, and they are not the same feature:

1. **Data-completeness notifications** — what CardiTrack is missing from an account, what supplying it unlocks, and the caregiver's comply / snooze / mute response. **Shipped, in-app only.**
2. **Push delivery** for health alerts — device registration, tokens, acknowledgement, escalation. **Shipped.**

The engine behind both is designed in [notification_engine.md](../../../technical/notification_engine.md). **Email and SMS are out of scope by decision:** escalation runs across recipients and devices, never vendors.

**User Stories:** 3.2 (Managing Alert Notifications), 5.1 (Mobile Push Notifications)

---

## Implemented today

The in-app inbox and its actions. All responses use the standard `ApiResponse<T>` envelope; enums serialize as integers.

| Endpoint | Notes |
|----------|-------|
| `GET /api/v1/notifications` | The caller's inbox, priority-ranked. Query params `state`, `category`, `cardiMemberId`, `owned`, `limit` (default 50, **clamped** into 1–200 rather than rejected), `offset` (floored at 0). Unrecognised `state`/`category` values are still rejected with **400** rather than silently ignored |
| `GET /api/v1/notifications/summary` | Unseen count, open count, safety banners and the two dashboard card slots in one call — what the app reads on launch |
| `POST /api/v1/notifications/{id}/seen` | Records first sighting; idempotent, and only the first counts. Drives the comply funnel's denominator |
| `POST /api/v1/notifications/{id}/snooze` | Body `{ "duration": "7.00:00:00" }`, optional. A *valid* duration past the rule's maximum is **clamped** rather than rejected, so a client asking for a month on a safety rule gets 72 hours and a success; an **unparseable or non-positive** duration is a **400** |
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

**Rule example — `DEVICE_BATTERY_LOW`:** the Safety-class battery rule shows what the full treatment looks like. It gets the Safety envelope — immediate push, critical APNs flag, quiet-hours override, 30-minute TTL, escalation — and fires in **three tiers**: Warning at **≤ 30%**, Urgent at **≤ 20%** (or a `Low` band with no percentage), Critical at **≤ 10%** or an `Empty` band. It is gated on battery data **fresh within 12 hours** (tightened from 24 h), and it is suppressed when a broken-grant notification outranks it: a device that cannot sync at all is the bigger problem, and the battery warning would be noise beside it. Copy differs per tier (`warning` / `urgent` / `urgent_unknown` / `critical` / `critical_empty`).

### Related — implemented alongside

`PUT /api/v1/users/me/timezone` — body `{ "timeZoneId": "Europe/London" }`. Added with the engine because the `TIMEZONE_DEFAULT` notification asks the user to set it and nothing could. Rejects ids the platform does not recognise with **400**.

### Superseded

The `UserCardiMember` link's old `NotificationPreferences` JSON column and its `weeklyDigest`/per-channel-email/SMS shape are gone — dropped in the `AddPushDeliverySpine` migration along with the rest of the design this section used to describe. This engine has never sent email or SMS and never will (§6 of the engine doc); the actual preference surface is quiet hours and lock-screen detail only, documented below.

---

## Push delivery contract

Everything below is real: GUID ids throughout, enums serialize as integers (matching the rest of this API), and every response uses the standard `ApiResponse<T>` envelope. Full design rationale lives in [notification_engine.md](../../../technical/notification_engine.md) §5–§8, §12.

### POST `/api/v1/notifications/devices`

Registers or upserts a device's FCM token for the authenticated user, and doubles as the reachability heartbeat — call it on launch after the permission prompt, and again on every foreground so `PUSH_UNREACHABLE` clears the moment notifications come back on.

**Auth:** Yes (Auth0)

**Request body:**

```json
{
  "deviceId": "device_abc123",
  "platform": 1,
  "token": "fcm-registration-token...",
  "appVersion": "2.0.1",
  "osAuthorizationStatus": 2,
  "safetyChannelEnabled": true
}
```

| Field | Type | Description |
|-------|------|-------------|
| `deviceId` | string | Stable per install, distinct from the token so a token rotation doesn't read as a new device |
| `platform` | int (`DevicePlatform`) | `1` = iOS, `2` = Android |
| `token` | string | The raw FCM registration token — never logged, stored encrypted at rest |
| `appVersion` | string | Diagnostics only |
| `osAuthorizationStatus` | int (`OsAuthorizationStatus`) | Read from the OS permission API |
| `safetyChannelEnabled` | bool | Whether the OS-level Safety channel (`carditrack.safety.v2`) is on; muting it at the OS level arms `PUSH_UNREACHABLE` the same as a revoked permission |

**Response `200 OK`:**

```json
{
  "id": "9b2f5f64-5717-4562-b3fc-2c963f66afa6",
  "deviceId": "device_abc123",
  "platform": 1,
  "osAuthorizationStatus": 2,
  "safetyChannelEnabled": true,
  "lastSeenDate": "2026-08-11T10:00:00Z"
}
```

Never carries the token itself in the response — it is Tier 1 data ([data_protection_architecture.md](../../../technical/data_protection_architecture.md) §2), and the client already knows its own token.

### DELETE `/api/v1/notifications/devices`

Unregisters a device — call on logout or when the user disables push in-app.

**Auth:** Yes | **Body:** `{ "deviceId": "device_abc123" }` | **Response:** `200 OK`

### POST `/api/v1/notifications/{notificationDeliveryId}/delivered`

The client's delivery acknowledgement — posted from the background push handler, before any user interaction. Halts the escalation ladder for this delivery.

**Auth:** None — `[AllowAnonymous]`. A background handler routinely runs with an expired Auth0 access token (1-hour lifetime, zero clock skew), so a session check would fire escalation for an alert that did arrive. Authorized instead by the payload's single-use `ackToken` (HMAC-SHA256, embeds and authenticates the device id, expires with the message).

**Body:** `{ "ackToken": "..." }`

**Response:** `200 OK` on a valid token; **`404`** on forged, expired, replayed, or other-device — non-disclosure, matching the [alerts.md](alerts.md) convention. A rejected token never halts escalation; only a valid one does.

### GET `/api/v1/notifications/preferences`

**Auth:** Yes

**Response `200 OK`:**

```json
{
  "quietHoursStart": "22:00:00",
  "quietHoursEnd": "07:00:00",
  "showDetailsOnLockScreen": false,
  "mutedCategories": ["Nudge"]
}
```

`quietHoursStart`/`End` are nullable — unset means no deferral window. Safety-category pushes always pierce quiet hours regardless.

### PUT `/api/v1/notifications/preferences`

**Auth:** Yes

**Request body:** same shape as the GET response. `showDetailsOnLockScreen` is opt-in richness (§7.1) — a caller that omits it gets `false`, never silently turned on. `mutedCategories` can never include `"Safety"`: the server strips it rather than trusting the client to omit it.

**Response:** `200 OK`, the updated preferences object.

---

## Internal push contract

`api/v1/internal/notifications/*` — service-to-service only, on a second, named JWT Bearer scheme (`GoogleOidc`) entirely separate from Auth0's default. Not under `api/v1/notifications`, and not reachable with a user's access token at all.

### POST `/api/v1/internal/notifications/enqueue`

Called by the AI pipeline's `SeverityRouter` once it has written an `Alert` row — this is the pipeline's transport into the same rules engine every other producer uses, not a copy of it. Recipient resolution, quiet hours, dedup and escalation all happen server-side inside `IDispatchService.EnqueueForAlertAsync`.

**Auth:** `GoogleOidc` scheme — issuer pinned to `https://accounts.google.com`, audience pinned to `Pipeline:Audience`, and the caller's verified `email` claim pinned to `Pipeline:ServiceAccount`. Audience-pinning alone would admit any GCP principal in the project that can mint a token for that audience; the email pin is what actually restricts the caller to the pipeline service account. Defense in depth beyond the OIDC scheme: the route also sits behind Cloud Run IAM (`roles/run.invoker` granted only to the pipeline service account).

**Body:** `{ "alertId": "..." }` — deliberately carries nothing else. Which users get notified is always resolved server-side from `UserCardiMember.ReceiveAlerts`, never trusted from the caller.

**Response:** `200 OK`, `{ "message": "Enqueued N deliveries." }`.

### GET `/api/v1/internal/notifications/{deliveryId}/content`

What the iOS notification service extension (or Android's data-message handler) fetches to rewrite a content-free push before display (§5, §7.1) — the real title/body a push payload deliberately omits.

**Auth:** None — `[AllowAnonymous]`, scoped by the query-string `fetchToken` instead of the controller's OIDC scheme, since this is called by a phone, not the pipeline. Never the user's access token (§7.2 C5), so the extension — a separate process spun up on every push — never holds a credential wider than this one delivery.

**Response `200 OK`:** `{ "title": "...", "body": "..." }`. **`404`** on an invalid/forged/replayed `fetchToken` or an unresolvable delivery.

> **Not shipped:** the extension itself (the Xcode/App Extension project that would call this). Its server-side dependency shipped; the extension needs Mac-based build/sign verification this environment doesn't have — see [notification_engine.md](../../../technical/notification_engine.md) §17. Until it lands, iOS renders the content-free teaser like Android does.

---

**Related:** [readme.md](readme.md) | [alerts.md](alerts.md) | [User Stories 3.2, 5.1](../../ui/mobile/user_stories.md)

**Last Updated:** August 14, 2026
