# Users API

The signed-in caregiver's own account settings — `api/v1/users/me/*`. Everything here is about the caller; nothing takes a user id.

## Endpoints

| Endpoint | Description |
|---|---|
| `PUT /api/v1/users/me/timezone` | Set the caller's IANA time zone — documented with the notification engine that needs it, in [notifications.md](notifications.md) |
| `GET /api/v1/users/me/health-data-disclosure` | Whether the caller has dismissed the Google-mandated health-data disclosure |
| `POST /api/v1/users/me/health-data-disclosure/dismiss` | Record that the caller has read it |

## Health-data disclosure

Google's restricted-scope verification requires an in-app disclosure, in Google's prescribed form, on every surface that collects or displays health data: *"CardiTrack collects health and fitness data to enable anomaly alerts, daily health digests, and trend monitoring."* The web app shows it in its layout until dismissed (PR #9); the mobile app shows it on the Dashboard (2026-09-13). Both record the dismissal on the user — `User.HealthDataDisclosureDismissedDate` — so it is acknowledged once per caregiver, not once per device, and the **first** acknowledgement's timestamp is the compliance record. The web app reaches `IUserService` in-process; these two endpoints are the same two calls over HTTP for mobile.

### GET `/api/v1/users/me/health-data-disclosure`

**Response:** `200 OK`

```json
{ "success": true, "message": "Here you go!", "data": { "dismissed": false }, "timestamp": "2026-09-13T12:00:00Z" }
```

A client shows the banner while `dismissed` is `false`. **Clients must not serve this from an offline cache** — a compliance banner decided by a stale answer is the wrong kind of last-known-good. The mobile client asks live on every dashboard appearance; when it cannot, it **shows** the banner (an unknown answer reads as "not yet told", never as "acknowledged"), and the only thing it keeps on the device is that the account once confirmed a dismissal, so an already-acknowledged caregiver is not re-shown it every time the phone is offline.

`403` when the caller has no identity.

### POST `/api/v1/users/me/health-data-disclosure/dismiss`

No body. **Response:** `200 OK` with the standard envelope and no data. Idempotent — a second dismissal keeps the first timestamp.

`404` when there is no user row for the caller's identity yet (the disclosure must keep showing rather than be recorded against nobody); `403` when the caller has no identity. A client hides the banner only on `200`.
