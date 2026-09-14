# Users API

The signed-in caregiver's own account settings — `api/v1/users/me/*`. Everything here is about the caller; nothing takes a user id.

## Endpoints

| Endpoint | Description |
|---|---|
| `PUT /api/v1/users/me/timezone` | Set the caller's IANA time zone — documented with the notification engine that needs it, in [notifications.md](notifications.md) |
| `GET /api/v1/users/me/health-data-disclosure` | Whether the caller has dismissed the Google-mandated health-data disclosure |
| `POST /api/v1/users/me/health-data-disclosure/dismiss` | Record that the caller has read it |
| `GET /api/v1/users/me/deletion` | Whether this account is awaiting deletion, and when |
| `POST /api/v1/users/me/deletion` | Ask for the account and its members' data to be deleted |
| `DELETE /api/v1/users/me/deletion` | Call an outstanding request off |

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

## Account deletion

Play's account-deletion policy and Apple's Guideline 5.1.1(v) both require deletion to be
reachable **inside the app**; the web form at `/delete-account` does not substitute for it. These
three endpoints are the server half. (The in-app screen that calls them is a later slice of #148 —
today the mobile app still opens a pre-addressed email.)

**A request, not an erasure.** The published privacy policy promises erasure *within 30 days of a
verified request*, and those 30 days are spent as a grace period rather than merely allowed as a
deadline: the request is recorded, and signing in again during the window calls it off. A
caregiver watching over a relative's health should be able to take back a 2am tap.

**Asking twice does not restart the clock.** A second request returns the first one's
`scheduledForUtc`. A restarted clock would fail in the one direction that matters — it would keep
the data longer than the caregiver was told it would be kept.

**What carries it out.** `RetentionWorker` in `CardiTrack.Worker` (M6), which runs daily at 05:00
UTC and erases every account whose 30 days have elapsed. It calls `IAccountErasureService`, which
erases the members this caregiver was the last active watcher of — via the same
`IMemberErasureService` cascade, 29 tables in one transaction plus the profile photo and any report
exports in GCS — releases the members somebody else still watches, and then removes the account's
own rows.

**Live in dev, not in prod.** CI run 34901698217 applied both migrations and deployed the Worker
on 2026-09-14, so these endpoints now lead to a real erasure in dev — though its first sweep runs
in rehearsal (`retention_worker_dry_run`), logging what it would erase rather than erasing it.
Prod has no deploy of this: there, a request recorded here still waits for the manual runbook. Until then, and for anything the worker reports as an orphaned storage object,
[manual_erasure_runbook.md](../../../technical/manual_erasure_runbook.md) is still the operational
path, and these endpoints are what tell an operator a request exists.

### GET `/api/v1/users/me/deletion`

**Response:** `200 OK`.

```json
{ "success": true, "message": "Here you go!", "data": {
  "deletionRequested": true,
  "requestedAtUtc": "2026-09-14T11:00:00Z",
  "scheduledForUtc": "2026-10-14T11:00:00Z",
  "canCancel": true
}, "timestamp": "2026-09-14T11:05:00Z" }
```

`canCancel` is true for the whole window — someone who changes their mind on day 29 is exactly who
it is for. `403` when the caller has no identity; `404` when there is no user row for it.

### POST `/api/v1/users/me/deletion`

No body. **Response:** `200 OK` with the same shape as `GET`.

Idempotent: a second request reports the first one's dates.

**A client that receives `200` should sign the caregiver out** — an account awaiting deletion has
no business going on monitoring anyone. It signs back in to cancel.

`403` / `404` as above.

### DELETE `/api/v1/users/me/deletion`

No body. **Response:** `200 OK`, `deletionRequested: false`.

Idempotent in the forgiving direction: cancelling when nothing was requested succeeds, because the
state the caller wanted is the state they get.

`403` / `404` as above.
