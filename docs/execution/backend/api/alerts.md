# Alerts API

> **Status: Partially implemented.** The P0 list/acknowledge/delete slice backing the mobile Alerts List (M1-10) ships in `AlertsController`; everything else below is still design intent. See "Implemented today" for exactly what exists.

Handles alert retrieval, acknowledgment, status lifecycle, photo attachments, and per-member alert notification preferences including quiet hours and sensitivity.

**User Stories:** 3.1 (Receiving Critical Alerts), 3.2 (Managing Alert Notifications), 3.3 (Alert Acknowledgment & Notes), 11.1 (Activity Alerts), 11.2 (Heart Rate Alerts), 11.3 (Pattern Break Alerts)

---

## Implemented today

### GET `/api/v1/insights/alerts/{alertId}` — AI alert analysis

On-demand **MedGemma analysis** of a single alert (explanation, severity, recommended action). Returns 200 with `ApiResponse<AlertInsightResponse>`, **404** for an unknown alert ID. This is an Insights endpoint, not part of `AlertsController`.

```json
{
  "alertId": "9b2f5f64-5717-4562-b3fc-2c963f66afa6",
  "explanation": "Margaret's step count dropped 50% below her 30-day baseline...",
  "severity": 2,
  "recommendedAction": "Consider a check-in call today."
}
```

> `severity` is the **integer** `AlertSeverity` enum (Green=1, Yellow=2, Orange=3, Red=4) — enums serialize as integers on the wire (see [readme.md](readme.md)).

### The M1-10 slice — `AlertsController`

Six endpoints are live, serving the mobile Alerts List and the alert detail screen:

| Endpoint | Notes |
|----------|-------|
| `GET /api/v1/alerts` | Query params `cardiMemberId`, `severity`, `status`, `from`, `to`, `limit` (default 50, max 200), `offset`. Scoped to the members the caller may read via `ICardiMemberAccessService`; an unreadable `cardiMemberId` returns **404**, not 403, for the usual non-disclosure reason. Unrecognised `severity`/`status` values are rejected with **400** rather than silently ignored. |
| `GET /api/v1/cardimembers/{id}/alerts` | Same filters, single member. |
| `GET /api/v1/alerts/{alertId}` | Detail for M1-11/12/16. Same view-access / 404-not-403 rule. Carries **one** chart series chosen from the alert's `rule` (steps for activity/trend/no-morning, resting HR for elevated HR, sleep hours for irregular sleep, granular HR for `realtime_hr`, HRV for `hrv_drop`, overnight breathing for `overnight_breathing_up`, raised-heart-rate minutes for `elevated_zone_without_movement`, longest still stretch in hours for `daytime_inactivity_block`). `device_silence` has no chart. Does **not** return the dashboard's six-metric payload. |
| `POST /api/v1/alerts/{alertId}/acknowledge` | No request body. Idempotent — re-acknowledging keeps the original timestamp and acknowledger, so a second family member tapping "handled" doesn't overwrite who dealt with it. |
| `DELETE /api/v1/alerts/{alertId}/acknowledge` | The undo. Clears `acknowledgedAt`/`acknowledgedBy` and returns the same `AlertAcknowledgementResponse` with `status: "new"` and a refreshed `unreadCount`. Same view-access bar as acknowledging — this restores an alert to everyone's attention rather than taking it away. Idempotent, and **400** for an alert the system has already resolved: resolution is CardiTrack's judgement that the condition passed, and a caregiver toggle must not reopen it. |
| `DELETE /api/v1/alerts/{alertId}` | Returns **204**. Removes the alert from the caregiver's lists — housekeeping, not a clinical action. Soft-delete (`IsActive = false`); producers still see the row so the same day's quieter steps (or the same silence/heart episode) cannot page again on the next tick. **404** on an unreachable alert (unknown or not the caller's to see). |

Response shape differs from the design below in three ways, all because the implemented `Alert` entity is what it is:

- `type` is the **`AlertType` display name** ("Inactivity", "Heart Rate", "Sleep", "Pattern Break", "Trend"), not the `activity_decline` string taxonomy.
- `severity` is the lowercase `AlertSeverity` name (`green`/`yellow`/`orange`/`red`), and `status` is derived from `AcknowledgedDate` + `IsResolved` rather than stored — see `AlertStatus`.
- Each summary carries `cardiMemberName`, `emergencyContactPhone` and `emergencyContactName` so the M1-10 card can render its avatar and Call action without a second round-trip. `cardiMemberPhotoUrl` is present but always null: no member photo storage exists yet. `aboutDate` is the civil day the alert is **about** — yesterday for `activity_decline` / `elevated_heart_rate` / `long_term_trend`, the night judged for `irregular_sleep`, the firing day otherwise. The list groups by `aboutDate`, not `triggeredAt`, so a quieter yesterday is not filed under Today because the worker noticed it this afternoon. `triggeredAt` remains the raise instant (relative "2 hours ago" on the card).

**Still not implemented:** status transitions (`PUT .../status`), notes, photos, and history. The M1-11 "More Options" rows follow the same line: `View Detailed Activity Data` and `Share with Family` ship because they need no backend, while `Adjust Baseline`, `Add Note About This Alert` and `Book a Doctor Visit` are absent from the screen entirely — there is no baseline-override endpoint, no `AlertNote` store, and no clinician or consent architecture behind them. Per-CardiMember alert preferences remain unbuilt too, though quiet hours and per-category push muting now exist **at user scope** — see "Sensitivity and preferences" below. Acknowledgment takes no `note`/`actionTaken` — notes would need a schema change (`AlertNote`).

Alert **summaries** also surface in the dashboard's `recentAlerts` array — see [health-data.md](health-data.md).

### Actual alert-type taxonomy

The implemented `AlertType` enum (integers on the wire) differs from the string taxonomy designed below:

| Value | Name | Meaning |
|-------|------|---------|
| 1 | `Inactivity` | Activity well below baseline |
| 2 | `HeartRate` | Resting HR outside normal range |
| 3 | `Sleep` | Sleep duration significantly off baseline |
| 4 | `PatternBreak` | Break from established daily pattern |
| 5 | `Trend` | Multi-week decline trend |

> **First automated producer (AI pipeline, dev):** the real-time assessor writes `HeartRate` alerts — MedGemma's red/orange verdict over the member's latest SSA-denoised hour, with the model's 1–3 sentence assessment as the `message` and the SSA yardsticks in `metricValues`. **Cooldown:** one *unresolved* `HeartRate` alert per member at a time; resolving it re-arms the path. A verdict the parser cannot read never becomes an alert. See [llm_design.md](../../../llm_design.md).
>
> **Second automated producer (Worker):** `InactivityDetectionWorker` writes `Inactivity` alerts (always `yellow`, rule-based text, no AI) when a device produces no granular readings for >2 h during the member's local waking hours — the designed `device_disconnected` scenario mapped onto the implemented enum, stamped `rule: device_silence`.
>
> **Third automated producer (Worker):** `StatisticalAlertWorker` — the R1 statistical engine — evaluates the nine-rule taxonomy below every 15 minutes against the **established 30-day baseline only**. Cooldown and dedup semantics are in the note under the taxonomy table.

### Sensitivity and preferences

**The nine built-in rules still run on fixed constants.** Their thresholds are the hard-coded "medium" profile below (deviation > 30% → yellow, > 50% → orange), and `CardiMember.AlertSensitivity` remains stored end-to-end and unused by every producer. Nothing about the shipped rules is tunable.

**What is tunable is a separate thing: caregiver-defined alarms.** `MetricAlarm` (R2) lets a caregiver say "tell me when this reading reaches this level" in the grammar cloud monitoring made standard — metric, statistic, comparison, threshold, evaluation window, M-of-N datapoints, missing-data treatment, severity — set once for the account and overridable per CardiMember. These **coexist with** the nine rules rather than retuning them: a firing alarm writes an ordinary `Alert` row with `rule: "custom:{alarmId}"` and inherits the whole delivery spine. See "User-defined alarms" below, and [alarm_catalogue.md](../../../technical/alarm_catalogue.md) for the suggested defaults and their sources.

**Per-CardiMember alert-rule enablement is shipped.** `GET /api/v1/cardimembers/{id}/alert-preferences` returns clustered rules with effective on/off state (missing preference row = all on). `PATCH .../alert-preferences/rules/{ruleId}` toggles one rule immediately. Off means the producer **skips evaluation entirely** for that rule — no `Alert` row. Primary caregiver only for writes; any viewer of the member may read. Future A–G rules appear in the catalogue with `isImplemented: false` until their producers land.

**Quiet hours and per-category push muting do exist** — but on the **user**, not per CardiMember: `GET`/`PUT /api/v1/notifications/preferences` carries quiet hours, lock-screen detail, and muted categories, with Safety-category pushes always piercing both quiet hours and mutes. See [notifications.md](notifications.md).

(The old `NotificationPreferencesRequest` DTO this section used to describe is deleted — replaced by `UpdateNotificationPreferenceRequest` behind `PUT /api/v1/notifications/preferences`.)

Everything below is the **planned** contract, kept as design intent.

---

## GET `/api/v1/alerts`

> **Implemented** — see "The M1-10 slice" above for how the live response differs from the design intent below.

List all alerts across all accessible CardiMembers.

**Priority:** P0 | **Auth Required:** Yes

### Query Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `cardiMemberId` | string | Filter by specific CardiMember |
| `severity` | string | `yellow`, `orange`, `red` |
| `status` | string | `new`, `acknowledged`, `resolved` |
| `from` | string (ISO 8601) | Start date filter |
| `to` | string (ISO 8601) | End date filter |
| `limit` | integer | Max results (default: 50, max: 200) |
| `offset` | integer | Pagination offset |

### Response `200 OK`

```json
{
  "alerts": [
    {
      "alertId": "alert_xyz_001",
      "cardiMemberId": "cm_01J8K2...",
      "cardiMemberName": "Margaret Doe",
      "type": "activity_decline",
      "severity": "yellow",
      "status": "new",
      "headline": "Margaret's activity is lower than usual",
      "description": "Margaret's steps: 2,500/day. Normal: 5,000/day (-50%). This could indicate illness, pain, or low mood.",
      "triggeredAt": "2026-03-09T09:00:00Z",
      "acknowledgedAt": null,
      "acknowledgedBy": null
    }
  ],
  "total": 1,
  "unreadCount": 1
}
```

**Alert Types** (the string taxonomy is now the `rule` discriminator each producer stamps into `MetricValues`; the implemented enum is the five-value integer `AlertType` in "Implemented today" above):

| Type (`rule`) | Severity | Implemented enum | Status | Description |
|------|---------------|------------------|--------|-------------|
| `activity_decline` | yellow | `Inactivity` (1) | **Built** (`StatisticalAlertWorker`) | Yesterday's steps >30% below the established 30-day baseline average |
| `elevated_heart_rate` | orange | `HeartRate` (2) | **Built** (`StatisticalAlertWorker`) | Yesterday's resting HR above baseline avg + max(2σ, 5 bpm) |
| `no_morning_activity` | red | `PatternBreak` (4) | **Built** (`StatisticalAlertWorker`) | A **measured zero** steps today past typical wake time + 2 h grace, while the device is reporting — a null steps value (HR-only device) never fires |
| `irregular_sleep` | yellow | `Sleep` (3) | **Built** (`StatisticalAlertWorker`) | Last night's sleep >30% off baseline average, either direction. Sleep sessions are attributed to the civil day they **ended** on, so last night is **today's** activity row — the same row the dashboard's sleep card rates — with yesterday's row as the fallback for a night whose data arrived after local midnight. The alert stamps the `night` it judged into `MetricValues` and dedups per night (not per firing day), so a late-synced night still alerts exactly once. **The trigger is symmetric; what it alerts on is not** — see the note below |
| `device_silence` | yellow | `Inactivity` (1) | **Built** (`InactivityDetectionWorker`) | No granular readings for >2 h during local waking hours (the scenario the design called `device_disconnected`) |
| `long_term_trend` | orange | `Trend` (5) | **Built** (`StatisticalAlertWorker`) | Weekly step average declining ≥5%/week for 4 consecutive weeks (each week needs ≥4 measured days) |
| `hrv_drop` | orange | `HeartRate` (2) | **Built** (`StatisticalAlertWorker`) | Overnight HRV (RMSSD) below baseline − max(2σ, 15% of baseline) on **both** of the last two nights. Two nights, because a single low night is a late meal or a glass of wine in someone with nothing wrong; the floor is proportional rather than absolute because RMSSD is not comparable between people |
| `overnight_breathing_up` | orange | `PatternBreak` (4) | **Built** (`StatisticalAlertWorker`) | Last night's *asleep* breathing rate above baseline + max(2σ, 1 breath/min). The overnight figure, not the daily one — a whole-day average moves with stairs and naps. Baseline-relative rather than band-relative: WHO's 12–20 is wide enough to hide a four-breath rise inside it, and the band is quoted in the copy for context only |
| `elevated_zone_without_movement` | orange | `HeartRate` (2) | **Built** (`StatisticalAlertWorker`) | Minutes above the light heart-rate zone past max(their usual, 25 min) on a day `activity_decline` also considers quiet. The pairing is the finding: the same minutes after a walk are exercise, and on a 1,200-step day they are a heart working without being asked to |
| `daytime_inactivity_block` | yellow | `Inactivity` (1) | **Built** (`StatisticalAlertWorker`) | One unbroken sedentary stretch past max(3 h, their usual + 50%). Reads `activity-level` as *intervals*: the day's sedentary total cannot tell six hours in half-hours from six hours at once, and only the second is worth a word |

> **Cooldown scope follows the family's remedy.** Every producer stamps its `rule` into `MetricValues`, and one *unresolved* alert per rule suppresses that rule (resolving re-arms it) — except `HeartRate`, which is deliberately **type-scoped across producers**: an unresolved heart alert from either the AI assessor (`realtime_hr`) or the statistical rule suppresses the other, because the remedy is the same ("check on them") and two simultaneous heart pages about one person is the noise cooldowns exist to prevent. Caregiver-defined alarms (`custom:{alarmId}`) sit **outside** the heart cooldown in both directions: their alert re-arms through the alarm's own state row and is never resolved by a producer, so it neither suppresses the assessor and the statistical heart rules nor is closed by the assessor's ordinary-hour pass — it suppresses only its own rule. `Inactivity`'s two rules (`device_silence`, `activity_decline`) ask for different remedies — charge the watch vs. encourage movement — and may stand together. Daily-grain statistical rules additionally dedup per local day: resolving **or deleting** at noon does not re-page at half past from the same readings (`irregular_sleep` dedups per **night judged** rather than per firing day — see its row). Soft-delete is housekeeping, not a new episode: the statistical engine still sees the deleted row for that same-day/same-night check (but not as a forever latch — it does not auto-resolve, so a deleted alert must not silence the rule on later days), while `device_silence` and `realtime_hr` treat a deleted unresolved alert as the same episode until their own "the condition has passed" resolve fires. **Provisional baselines never fire any statistical rule** — the engine fetches only the established 30-day baseline, so a member without one is silent by construction.

> **`irregular_sleep` alerts by direction, against the published band.** A departure from the member's own usual cannot say on its own whether a night was a problem, because the usual it is measured against may itself be far short of what anyone should be getting: someone who normally manages 3.8 h and slept 5.2 h is 37% off their baseline *and* closer to the recommendation than they have been all fortnight. That is an improvement, and it is retrospective — the night is over by the time anyone reads about it. So a **longer** night that has not passed the NSF ceiling for the member's age (`HealthReferenceRanges.Sleep`: 7–9 h, 7–8 h from 65) now **raises no alert at all**: the fact belongs in the daybook entry, which describes the finished day, rather than on a screen whose job is to say what needs attention now. Past that ceiling it is `yellow`, the one direction in which more sleep is worth flagging. A **shorter** night keeps `yellow` whatever the absolute figure, because losing a third of someone's sleep overnight is a pattern break in its own right. *(Alerts raised by the retired benign branch were resolved — not deleted — by the `RetireBenignSleepAlerts` migration; this engine never auto-resolves, so they would otherwise have stood for good.)* The band the night was judged against is stamped into `MetricValues` (`recommendedLowHours` / `recommendedHighHours`) so a member who later crosses 65 cannot get copy quoting one ceiling beside a chart shading another.

> Severities use the product taxonomy (`yellow`/`orange`/`red`), plus `green`, which now reaches a caregiver only from the **AI assessor** (`Low` maps there). No statistical rule emits it any more: `irregular_sleep` was the only one that could, and its benign branch was retired to the daybook entry. Historic `green` rows remain on file and still render, so clients must keep handling the value. `green` is *not* emitted for merely normal states: no alert is raised when nothing departed from baseline at all. **A `green` alert is a full alert everywhere except the status tier.** Delivery is unchanged by the split — `DeliveryPlanner` already routes both `yellow` and `green` Health rows to in-app + digest and pushes neither. It still forces a digest refresh when newly raised (`DigestGenerationService.AlertStateChangedSinceAsync` gates on `TriggeredDate`, not on severity) and still reaches the digest prompt while unresolved (`MonitoringContextSource` applies its `Yellow` floor to real-time *assessments*; alerts are carried on `!IsResolved` alone). The one thing it does not do is raise the member's dashboard status tier: `ComputeHealthStatus` only takes a label from the worst unresolved alert at `Yellow` and above, so a lone `green` alert leaves the tier where it would have been. Treat "green" as *we looked and it was fine*, not as *we said nothing*. The AI pipeline's internal Critical/High/Medium/Low scale maps to these values — see [llm_design.md](../../../llm_design.md).

---

## GET `/api/v1/cardimembers/{id}/alerts`

> **Implemented.**

List alerts for a specific CardiMember.

**Priority:** P0 | **Auth Required:** Yes

Supports the same query parameters as `GET /api/v1/alerts` (except `cardiMemberId`).

### Response `200 OK`

Same schema as `GET /api/v1/alerts`.

---

## GET `/api/v1/alerts/{alertId}`

> **Implemented** — see "The M1-10 slice" above. The live payload is `AlertDetailResponse`: list fields plus `rule`, `reason`, `phone`, `acknowledgedByName`, a single `chart` (or null), `comparison`, and silence/no-morning context. It does not return `recommendedActions`, `notes`, or `photos`.

`reason` is a coarse key for the detail screen's icon — `activity`, `heart`, `sleep`, `device` or `monitoring` — derived from `rule`, falling back to `AlertType` for rows written before rule markers. `hrv_drop` and `elevated_zone_without_movement` take `heart`, `daytime_inactivity_block` takes `activity`, and `overnight_breathing_up` takes `monitoring` — the icons are hand-authored and there is no lungs artwork, so filing a breathing finding under the heart icon would name the wrong organ at a glance. It is deliberately *not* the severity: the banner already carries that in colour, so an icon repeating it says nothing new. Clients should fall back to `monitoring` for an unrecognised value rather than rendering no icon.

**The day in progress.** For the step rules (`activity_decline`, `no_morning_activity`, `long_term_trend`) the chart window runs up to today, which has not finished. Three things follow:

- `chart.value` is the last **finished** day, and `chart.valueLabel` names it ("Yesterday") — so the chart header and the `comparison` block below it quote the same day. It used to be the latest reading outright, which meant a lunchtime step count in the header and the whole of yesterday in the comparison, with nothing on screen saying they were different days.
- `aboutDate` is that same finished day. The banner and the list's Today/Yesterday buckets follow it, not `triggeredAt`, so an `activity_decline` raised at 13:22 today about yesterday's steps is filed and dated as yesterday.
- The trailing `chart.series` point carries `isPartial: true`. Clients must draw it apart from the completed days.
- `chart.partialDayLabel` is the like-for-like sentence: "865 steps so far today, 22% below the 1,102 by this time yesterday". Both figures are summed from `MetricRollupsHourly` over the **same** run of whole hours since local midnight on the member's anchor clock (`MemberAnchorTimeZone` — the same clock `StatisticalAlertService` evaluated the rule on); rollups rather than minute vectors, because the two stretches together reach ~48 hours by late evening and this endpoint is re-polled the whole time the page is open. Null when the day in progress has too few covered hours to report, and the comparison clause is dropped when either day covers less than 75% of the elapsed hours — a day the watch spent off the wrist is not the same stretch as a day it was worn, and comparing them would only swap one unfair comparison for another.

Heart-rate and sleep charts set none of these: a resting heart rate and a night's sleep are settled figures when reported, so the calendar day having hours left in it does not make them running totals.

**Priority:** P0 | **Auth Required:** Yes

### Path Parameters

| Parameter | Description |
|-----------|-------------|
| `alertId` | Alert ID |

### Response `200 OK`

```json
{
  "alertId": "alert_xyz_001",
  "cardiMemberId": "cm_01J8K2...",
  "cardiMemberName": "Margaret Doe",
  "type": "no_morning_activity",
  "severity": "red",
  "status": "new",
  "headline": "Margaret hasn't moved today",
  "description": "Margaret hasn't moved today. Typical wake time: 7:00am. Current time: 11:00am.",
  "context": {
    "lastActivityAt": "2026-03-08T22:45:00Z",
    "typicalWakeTime": "07:00",
    "currentTime": "11:00",
    "frequencyNote": "This is the first time this month."
  },
  "recommendedActions": [
    {
      "id": "call",
      "label": "Call now",
      "actionType": "phone_call",
      "isPrimary": true
    },
    {
      "id": "check_in_person",
      "label": "I'm checking in person",
      "actionType": "acknowledge_with_note",
      "isPrimary": false
    },
    {
      "id": "dismiss_with_note",
      "label": "He told me he'd sleep in today",
      "actionType": "acknowledge_with_note",
      "isPrimary": false
    }
  ],
  "triggeredAt": "2026-03-09T09:00:00Z",
  "acknowledgedAt": null,
  "acknowledgedBy": null,
  "notes": [],
  "photos": []
}
```

### Errors

| Code | Status | Description |
|------|--------|-------------|
| `ALERT_NOT_FOUND` | 404 | Alert ID not found or not accessible |

---

## POST `/api/v1/alerts/{alertId}/acknowledge`

> **Partially implemented** — acknowledgment works and is idempotent; the optional note, `actionTaken`, and the family notification are not built.

Acknowledge an alert with an optional note. Notifies all other family members that the alert has been handled.

**Priority:** P0 | **Auth Required:** Yes

### Request Body

```json
{
  "note": "Called, she had a cold but is fine.",
  "actionTaken": "call"
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `note` | string | No | Free-text note about action taken |
| `actionTaken` | string | No | ID from `recommendedActions` (for analytics) |

### Response `200 OK`

```json
{
  "alertId": "alert_xyz_001",
  "status": "acknowledged",
  "acknowledgedAt": "2026-03-09T11:15:00Z",
  "acknowledgedBy": {
    "userId": "usr_01J8K2...",
    "name": "Jane Doe"
  },
  "note": "Called, she had a cold but is fine.",
  "familyNotified": true
}
```

---

## PUT `/api/v1/alerts/{alertId}/status`

Update alert status. Follows the lifecycle: `new` → `acknowledged` → `resolved`.

**Priority:** P1 | **Auth Required:** Yes

### Request Body

```json
{
  "status": "resolved",
  "note": "Doctor confirmed — minor infection, now recovering."
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `status` | string | Yes | `acknowledged` or `resolved` |
| `note` | string | No | Resolution note |

### Response `200 OK`

```json
{
  "alertId": "alert_xyz_001",
  "status": "resolved",
  "resolvedAt": "2026-03-10T14:00:00Z"
}
```

### Errors

| Code | Status | Description |
|------|--------|-------------|
| `INVALID_STATUS_TRANSITION` | 422 | Cannot transition from current status to requested status |

---

## POST `/api/v1/alerts/{alertId}/photos`

Attach a photo to an alert (e.g. a photo from a doctor visit).

**Priority:** P2 | **Auth Required:** Yes

### Request Body (`multipart/form-data`)

| Field | Type | Description |
|-------|------|-------------|
| `photo` | file | JPEG/PNG, max 10MB |
| `caption` | string | Optional caption |

### Response `201 Created`

```json
{
  "photoId": "photo_abc123",
  "url": "https://cdn.carditrack.com/alert-photos/photo_abc123.jpg",
  "caption": "Doctor visit summary",
  "uploadedAt": "2026-03-10T14:05:00Z"
}
```

---

## GET `/api/v1/alerts/{alertId}/history`

Get historical frequency data for the same alert type on this CardiMember. Provides context for caregivers ("This is the first time this month").

**Priority:** P1 | **Auth Required:** Yes

### Response `200 OK`

```json
{
  "alertId": "alert_xyz_001",
  "type": "no_morning_activity",
  "cardiMemberId": "cm_01J8K2...",
  "history": {
    "last7Days": 0,
    "last30Days": 1,
    "last90Days": 2,
    "frequencyNote": "This is the first time this month.",
    "previousOccurrences": [
      {
        "alertId": "alert_abc_002",
        "triggeredAt": "2026-02-14T09:15:00Z",
        "status": "resolved"
      }
    ]
  }
}
```

---

## GET `/api/v1/cardimembers/{id}/alert-preferences`

Get the clustered alert-rule catalogue for a CardiMember, with effective enablement. Missing preference rows mean every rule is **on**.

**Priority:** P1 | **Auth Required:** Yes (view access)

### Response `200 OK`

```json
{
  "cardiMemberId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "clusters": [
    {
      "id": "sleep",
      "title": "Sleep",
      "description": "Bedtime, sleep quality, and unusual daytime rest",
      "rules": [
        {
          "id": "irregular_sleep",
          "title": "Unusual sleep length",
          "description": "Last night was much shorter or longer than usual",
          "enabled": true,
          "isImplemented": true
        },
        {
          "id": "late_bedtime",
          "title": "Late or missed bedtime",
          "description": "Still active past their usual bedtime",
          "enabled": true,
          "isImplemented": false
        }
      ]
    }
  ]
}
```

---

## PATCH `/api/v1/cardimembers/{id}/alert-preferences/rules/{ruleId}`

Instant toggle for one rule. Off skips producer evaluation entirely. Unimplemented catalogue ids (`isImplemented: false`) cannot be toggled.

**Priority:** P1 | **Auth Required:** Yes | **Required:** primary caregiver (manage access)

### Request Body

```json
{
  "enabled": false
}
```

### Response `200 OK`

Returns the updated `AlertRuleSettingResponse` for that rule.

---

## User-defined alarms — `MetricAlarmsController`

> **Implemented (R2).** Distinct from alert *preferences* above: those switch CardiTrack's own rules on and off, these are the caregiver's own thresholds.

Scope lives in one table. A `MetricAlarm` row with a null `cardiMemberId` is an **account-level default** every CardiMember inherits; a row with one set applies to that member alone. A member row naming an account row in `derivedFromAlarmId` **replaces** it for that member — and replacing it with `isEnabled: false` is how a member opts out of an inherited alarm. A member row naming nothing is an addition.

| Endpoint | Notes |
|----------|-------|
| `GET /api/v1/alarms/catalogue` | Which metric × statistic × period combinations are evaluable, the threshold band each metric allows, and whether it supports baseline-relative thresholds or the stillness gate. The builder reads this so an illegal alarm is unreachable rather than refused. |
| `GET /api/v1/alarms` | The organization's account-level defaults. |
| `POST /api/v1/alarms` | Creates one. **400** on an illegal combination, on a threshold outside the metric's band, or on `severity: red` without `confirmCriticalSeverity`. |
| `PUT /api/v1/alarms/{alarmId}` | Replaces one. When the **condition** changed — metric, statistic, comparison, threshold, window, M-of-N, missing-data treatment, gate, or the on/off switch — every member's evaluation state for it is cleared, so a retuned alarm neither re-fires on a condition it was already standing on nor stays silent about one it now considers a breach. A rename or a change of severity keeps the states, so it does not re-page every member the alarm is standing on. |
| `DELETE /api/v1/alarms/{alarmId}` | **204.** Soft-delete. Members who had tuned it **keep their own copy** — a caregiver's tuning for one person is an intention about that person, and removing the shared default is not a retraction of it. |
| `GET /api/v1/cardimembers/{id}/alarms` | The **effective** set: account defaults folded together with this member's overrides and additions. Each row carries `provenance` (`Inherited` / `Overridden` / `MemberOnly`), its `condition` as one composed sentence, and its current `state` (`Ok` / `Alarm` / `InsufficientData`). |
| `POST /api/v1/cardimembers/{id}/alarms` | Adds an alarm for this member alone. |
| `PUT /api/v1/cardimembers/{id}/alarms/{alarmId}` | Given an account default's id, writes this member's override of it; given a member alarm's own id, edits that row. Same state-reset rule as the account-level PUT. An override saved back to **exactly what the account default says** — or a disabled override switched back on with nothing else changed, which is what the list page's toggle sends — is removed rather than kept, and the default applies again: a detached copy marked "tuned for them" that silently stops following account edits is not what the caregiver asked for. **409** when two devices write the same first override at once; the other save stood. |
| `DELETE /api/v1/cardimembers/{id}/alarms/{alarmId}` | **204.** Reverts an override to the account default, or deletes a member-only alarm. Accepts either identity so a client holding one list row need not know which it has. |

Reads require view access; **writes require primary-caregiver authority** — over that member for a member row, and over at least one member in the organization for an account-level default, since an account default reaches every one of them. Denial is **404, not 403**, the same non-disclosure convention as the rest of this API.

### Semantics worth knowing

- **An alert is written on the transition into alarm, never on the state.** `MetricAlarmState` carries that across ticks. Only a return to normal re-arms the alarm: a standing episode that dips through `InsufficientData` (the watch off for a quarter of an hour) and comes back is the same episode, not a second page — the row keeps the episode's `lastAlertId` until `Ok` clears it. Deliberately *not* the alert lifecycle: acknowledging a card says the caregiver has read it, not that the heart rate has come down.
- **Daily readings that accumulate through the day are judged on the last completed day.** Steps, raised heart-rate minutes and the longest still stretch climb from zero as the day goes on, so today's row is a partial figure until midnight; anchoring a "below" alarm on it would page every morning. Readings that are whole when filed — sleep-derived figures, resting heart rate, the daily SpO₂ average — use the freshest row.
- **Missing data has three verbs, not CloudWatch's four.** `Missing` (default) reports insufficient data; `NotBreaching` counts a gap as normal; `Ignore` holds the current state. **`Breaching` is not offered** — treating absence as over the line turns "the watch is off the wrist" into a 3am page and contradicts the null-vs-zero discipline. Data absence keeps its own producer (`device_silence`).
- **The evaluation window ends at the last reading, not at the clock.** Ingestion polls every ten minutes, so anchoring to wall-clock time would leave the newest datapoint permanently missing and make short alarms unfireable.
- **Baseline-relative thresholds resolve against the established 30-day baseline only.** No 30-day row means `InsufficientData`, never a fire — the provisional-never-alerts rule, reached the same way it is for the built-in rules.
- **Hysteresis:** a standing alarm clears only once the reading comes 5% back inside the threshold, so a value sitting on the line does not page on every crossing.
- **Ceiling of 12 enabled alarms per member**, with the client advising past 6.

Alarms are evaluated by `MetricAlarmWorker` every five minutes — non-AI polling, so the Worker per [CLAUDE.md](../../../../CLAUDE.md).

---

## ~~PUT `/api/v1/cardimembers/{id}/alert-preferences`~~ (superseded)

The bulk PUT that carried channels / quiet hours / family routing is superseded by the GET + per-rule PATCH above, plus user-scoped quiet hours on [notifications.md](notifications.md). SMS/email channels remain out of scope.

**Sensitivity Values** (design intent — today only the `medium` thresholds exist, as fixed constants; see "Implemented today"):

| Value | Description |
|-------|-------------|
| `low` | Only trigger alerts on large deviations (>50% from baseline) |
| `medium` | Standard thresholds (>30% deviation) — **current hard-coded behavior** |
| `high` | Sensitive thresholds (>15% deviation) |

> **Provisional baselines never fire alerts.** The dashboard may colour metrics against a 7- or 14-day *provisional* baseline (`baseline.isProvisional` in [health-data.md](./health-data.md)) so the first weeks are not silent, but deviation alerts threshold only against the **established 30-day** `PatternBaseline` — a statistically thin window would trade the product's <5% false-positive target for early noise, and false alarms erode trust faster than a missed one builds it.

---

**Related:** [readme.md](readme.md) | [notifications.md](notifications.md) | [family.md](family.md) | [User Stories 3.1, 3.2, 3.3, 11.1–11.3](../../ui/mobile/user_stories.md)

**Last Updated:** September 6, 2026
