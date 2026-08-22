# Health Data API

Provides health metrics, baselines, and dashboard data for caregivers.

**Implementation status:** the **per-member dashboard** below is implemented, plus **five AI endpoints under `/api/v1/insights`**: `alerts/{alertId}` (see [alerts.md](alerts.md)), `members/{id}/baseline`, `members/{id}/status`, `members/{id}/digest`, and `members/{id}/digests`. The multi-member dashboard, daily summary, trends, numeric baseline, batch ingestion, and export endpoints are **planned — not yet implemented** and marked as such.

> **Beneath these endpoints:** minute-grain granular storage exists (`GranularMetric`: HeartRate, Steps, ActiveZoneMinutes, SpO2 — 90-day granular retention, 13-month rollups) — see [granular_timeseries_storage.md](../../../technical/granular_timeseries_storage.md).

**User Stories:** 2.1 (Daily Health Overview), 2.2 (Multi-Member Dashboard), 2.3 (Trend Charts), 5.2 (Mobile Widget), 6.3 (Health Data Export), 10.1 (Offline Support)

---

## GET `/api/v1/cardimembers/{id}/dashboard` — implemented

Composed dashboard payload for **one CardiMember** (mobile Main Dashboard, M1-09): hero status, key metrics with 30-day series, recent alerts, and device/baseline state in a single round-trip. (There is **no** account-wide `GET /api/v1/dashboard` — the multi-member view is assembled client-side from `GET /api/Onboarding/cardimembers` plus per-member calls.)

**Priority:** P0 | **Auth Required:** Yes (active `UserCardiMember` link with `CanViewHealthData`)

### Path Parameters

| Parameter | Description |
|-----------|-------------|
| `id` | CardiMember ID (GUID) |

### Response `200 OK`

Wrapped in the standard `ApiResponse<T>` envelope:

```json
{
  "cardiMemberId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "name": "Margaret Doe",
  "age": 80,
  "phone": "+15551234567",
  "emergencyContactName": "Jane Doe",
  "emergencyContactPhone": "+15551234568",
  "photoUrl": null,
  "healthStatus": "yellow",
  "monitoringPaused": false,
  "monitoringPausedUntil": null,
  "monitoringPauseReason": null,
  "lastSyncedAt": "2026-08-07T08:30:00Z",
  "dataFreshness": "green",
  "dataFreshnessMessage": "Up to date",
  "unreadAlertCount": 1,
  "device": {
    "hasActiveConnection": true,
    "deviceType": "Fitbit",
    "deviceName": "Fitbit Charge 6",
    "connectionStatus": "Connected",
    "lastSyncDate": "2026-08-07T08:30:00Z"
  },
  "baseline": {
    "isLearning": false,
    "isProvisional": false,
    "baselinePeriodDays": 30,
    "daysCaptured": 30,
    "daysRequired": 30,
    "percentComplete": 100
  },
  "metrics": {
    "steps": {
      "value": 2500,
      "baseline": 5000,
      "changePercent": -50.0,
      "unit": "steps",
      "status": "yellow",
      "goal": 5000,
      "rangeLow": null,
      "rangeHigh": null,
      "qualityScore": 2,
      "series": [
        { "date": "2026-08-01", "value": 4800 },
        { "date": "2026-08-07", "value": 2500 }
      ],
      "reference": null
    },
    "restingHeartRate": {
      "value": 68,
      "baseline": 65,
      "changePercent": 4.6,
      "unit": "bpm",
      "status": "green",
      "goal": null,
      "rangeLow": 61,
      "rangeHigh": 69,
      "qualityScore": 5,
      "series": [ { "date": "2026-08-07", "value": 68 } ],
      "reference": { "low": 60, "high": 100, "source": "AHA" }
    },
    "sleep": {
      "value": 7.2,
      "baseline": 7.5,
      "changePercent": -4.0,
      "unit": "hours",
      "status": "green",
      "goal": null,
      "rangeLow": null,
      "rangeHigh": null,
      "qualityScore": 4,
      "series": [ { "date": "2026-08-07", "value": 7.2 } ],
      "reference": { "low": 7, "high": 9, "source": "NSF" }
    },
    "temperature": {
      "value": -0.2,
      "baseline": 0.0,
      "changePercent": null,
      "unit": "°C",
      "status": "green",
      "goal": null,
      "rangeLow": null,
      "rangeHigh": null,
      "qualityScore": 5,
      "series": [ { "date": "2026-08-07", "value": -0.2 } ],
      "reference": null
    },
    "spO2": {
      "value": 96,
      "baseline": null,
      "changePercent": null,
      "unit": "%",
      "status": "unknown",
      "goal": null,
      "rangeLow": null,
      "rangeHigh": null,
      "qualityScore": null,
      "series": [ { "date": "2026-08-07", "value": 96 } ],
      "reference": { "low": 94, "high": 100, "source": "WHO" }
    },
    "breathingRate": {
      "value": 14,
      "baseline": null,
      "changePercent": null,
      "unit": "brpm",
      "status": "unknown",
      "goal": null,
      "rangeLow": null,
      "rangeHigh": null,
      "qualityScore": null,
      "series": [ { "date": "2026-08-07", "value": 14 } ],
      "reference": { "low": 12, "high": 20, "source": "WHO" }
    }
  },
  "recentAlerts": [
    {
      "alertId": "9b2f5f64-5717-4562-b3fc-2c963f66afa6",
      "type": "Inactivity",
      "severity": "yellow",
      "title": "Margaret's activity is lower than usual",
      "message": "Steps well below baseline for two days.",
      "triggeredAt": "2026-08-07T09:00:00Z",
      "isAcknowledged": false
    }
  ],
  "generatedAt": "2026-08-07T10:00:00Z"
}
```

Field notes:

- There are **seven metrics** — `steps`, `restingHeartRate`, `sleep`, `temperature` (nightly *skin* temperature, compared against the device's own nightly baseline), `spO2`, `breathingRate` and `heartRateVariability` (overnight RMSSD in ms, against the member's own learned baseline and no published band — none exists for adult RMSSD). The last arrived on 2026-08-22 and is null-valued for a device that derives no HRV. The sleep key is **`sleep`** (not `sleepHours`); there is **no `activeMinutes` metric**.
- `photoUrl` is always `null` today (no photo storage exists).
- `metrics` is **`null`** when the member has no activity logs in the last 30 days.
- `dataFreshness` is deterministic **data-pipeline** freshness, deliberately independent of `healthStatus`'s clinical severity: `red` = no sync in 12 h, `amber` = no sync in 4 h, `blue` = synced but not yet assessed, `green` = the latest sync has been assessed. `dataFreshnessMessage` is its human-readable caption.
- Each metric resolves **independently**, from the most recent day that actually reported it. Ingestion stores the day in progress, so today's row appears as soon as the provider reports anything at all; a metric it has not filled in yet falls back to the last day that carried one rather than blanking a card that was populated a moment ago. This is the same per-metric coalescing the multi-device merge applies, applied across days.
- Each metric carries a **30-day** `series` (`MemberInsightsCalculator.SeriesDays = 30`) of `{date, value}` points ending **today** (missing days → `value: null`). Clients showing a shorter window (7/14 days) take the tail.
- `device.connectionStatus` is the internal enum name (`Connected`, `TokenExpired`, …) — unlike the lowercase statuses in [devices.md](devices.md).
- `recentAlerts` holds the **5 most recent** active alerts; `unreadAlertCount` counts unresolved, unacknowledged alerts.
- `goal` on steps is the member's **own baseline average** and **`null` until a baseline exists** — there is no 10 000 fallback, because no standards body publishes a daily step count and a round number would be our arithmetic under nobody's authority. The dashboard Activity bar does **not** fill against `goal`; it compares day n to the previous calendar day's total (max is yesterday until today exceeds it, then the max is today). When today is ahead the extra is a second stacked fill, so beating yesterday is not a full bar that looks like matching it. A missing day n−1 hides the bar. `goal` remains the usual-day figure for captions and explainers. `rangeLow`/`rangeHigh` are heart-rate mean ± one standard deviation; `qualityScore` is the 1–5 star rating described below.
- `reference` is the **published typical-adult range** for the metric — the population counterpart to `baseline`, which is this member's own learned normal. Described below.

**Health Status Values** (`healthStatus` and per-metric `status` — lowercase strings):

| Value | Meaning |
|-------|---------|
| `green` | All metrics within normal range |
| `yellow` | Minor deviation — worth monitoring |
| `orange` | Notable deviation — recommend action |
| `red` | Critical alert — immediate attention |
| `unknown` | Insufficient data (baseline still learning) |
| `paused` | Monitoring is paused — deliberately outside the severity scale, because a paused member has no current health colour |

**Deviation thresholds and baseline window** (fixed constants, no per-user sensitivity settings):

- Baselines come in three states. `isLearning` is true only while **no** baseline exists at all. From about the first week, a **provisional** baseline (7- or 14-day window, longest available preferred) colours the metrics with `isProvisional: true` and `baselinePeriodDays` naming the window — an early impression clients should caveat, and **never a source of alerts**. The established **30-day window** (`daysRequired: 30`) takes over as soon as it exists.
- Per-metric status: deviation from baseline **≤ 30%** → `green`, **> 30%** → `yellow`, **> 50%** → `orange`. (`red` comes only from alert severity, not metric deviation.)
- `steps` reports `changePercent: null` and `status: "unknown"` while its value covers **today**. Steps accumulate through the day, so scoring a part-finished day against a whole-day average would report every member as collapsing every morning; the dashboard bar compares that running total to yesterday instead. `restingHeartRate` and `sleep` are daily summary values rather than running totals, so a today reading is a whole reading and stays comparable.
- The member-level `healthStatus` is the worst unresolved alert severity, else `green` (or `unknown` while learning / no data).

**`qualityScore` — the 1–5 star rating** (rendered as the star row on each Key Metrics card):

| Metric | Rated on | `null` when |
|--------|----------|-------------|
| `steps` | Shortfall against the baseline average. Beating it is **not** marked down — 5 stars at or above normal | The day is still in progress, or no baseline exists |
| `restingHeartRate` | Deviation from baseline, **both directions** — unusually low counts as much as unusually high | No baseline exists |
| `sleep` | The **worse** of sleep efficiency (≥ 90 → 5, ≥ 80 → 4, ≥ 70 → 3, ≥ 60 → 2, else 1) and the **shortfall in duration** against baseline — either alone where the other is unavailable — then **capped on the length of the night** against the published band for the member's age: inside it → 5, then one star per hour outside it (≤ 1 h → 4, ≤ 2 h → 3, ≤ 3 h → 2, else 1). **Both ends**, so 4.5 h and 12 h are both marked down | Neither an efficiency nor a sleep baseline exists |
| `temperature` | Distance from the device's own nightly baseline in units of its nightly variation: ≤ 0.5σ → 5, ≤ 1σ → 4, ≤ 1.5σ → 3, ≤ 2σ → 2, else 1 | The device reports no baseline/variation |
| `spO2`, `breathingRate` | — always `null` | Always: no baseline concept exists for these yet, and rating them would mean inventing a normal |
| `heartRateVariability` | The shared percent-of-baseline bands, against the member's own learned overnight average | No established HRV baseline yet |

- For `steps` and `restingHeartRate`, the percentage-deviation bands (`≤ 5%` → 5, `≤ 15%` → 4, `≤ 30%` → 3, `≤ 50%` → 2, else 1) **nest inside** the status thresholds above, so the rating and the status can never contradict each other: 3–5 stars is `green`, 2 is `yellow`, 1 is `orange`.
- **The other two rated metrics do not use that mapping**, so a client cannot derive a star-row colour from the star count alone:
  - `temperature` is rated in units of σ on bands one step finer than its own status thresholds, giving **4–5 `green`, 2–3 `yellow`, 1 `orange`** — a 3-star reading (1–1.5σ) sits under a `yellow` (UNUSUAL) pill.
  - `sleep` has no fixed mapping at all, because its rating reads two things its `status` does not — how well the night was slept, and whether it was long enough at all. A habitually short sleeper can show **two stars beside a `green` (NORMAL) pill**, and both are true: the night was normal *for them*, and it was still not enough sleep.
- **Client rule for colouring the star row:** where the card shows a pill built from `status` (heart rate, skin temp), take the pill's colour, so two accents an inch apart cannot disagree. Elsewhere take 3–5 `green`, 2 `yellow`, 1 `orange` off the star count — `steps`, which shows no pill, and `sleep`, whose pill is itself named from those bands (**GOOD** 3–5 / **FAIR** 2 / **POOR** 1, hidden when unrated), so its pill and stars agree by construction. Never derive anything on the sleep card from `status`: it reads duration against the baseline alone, and using it is what paints a short sleeper's two stars green. The sleep pill uses a quality vocabulary rather than NORMAL/UNUSUAL because the rating is not purely member-relative — a 4.5-hour night is entirely *usual* for a member who always sleeps 4.5 hours, and its pill still reads FAIR.
- **Why sleep is capped on a published range at all**, when `reference` is otherwise presentational only: every member-relative comparison available is blind to the length of the night. Efficiency is a ratio — 4.4 hours asleep out of 4.5 in bed is 98%, five stars for a night nowhere near long enough — and the baseline of someone who habitually sleeps 4.5 hours says a 4.5-hour night is exactly normal, which is the very reading a caregiver is watching for. The cap can only ever **lower** a rating the member's own data already earned; it never raises one, and never creates one where there was nothing to rate (a duration with no efficiency and no baseline stays `null`). An unusual night is reported as an unusual night, not named a disorder.
- **The cap is the only thing that reads a night as too long.** The duration comparison it sits on top of counts shortfalls only, so an overshoot of any size scores 5 — deliberately, because a member catching up after a bad week has not earned a worse rating. That leaves the published ceiling as the only check on a 12-hour night, and it is age-split (8 h from 65, 9 h below), so the same night can rate differently either side of that line.
- A `null` score means "not rated", not "rated zero" — clients hide the star row entirely rather than showing five empty stars.

**`reference` — the published typical-adult range** (drawn as a shaded band behind the trend charts, beside the dashed rule at `baseline`):

| Metric | `reference` | `source` |
|--------|-------------|----------|
| `restingHeartRate` | 60–100 bpm | `AHA` — normal adult resting heart rate |
| `sleep` | 7–9 hours, or **7–8 from age 65** | `NSF` — recommended nightly sleep, adults / older adults |
| `spO2` | 94–100 % | `WHO` — pulse oximetry guidance (90–93 % hypoxaemia, < 90 % severe) |
| `breathingRate` | 12–20 brpm | `WHO` — Basic Emergency Care, adult respiratory rate |
| `steps`, `temperature`, `heartRateVariability` | `null` | No body publishes an adult band for overnight RMSSD — it spans an order of magnitude between healthy adults and falls steeply with age, so the member's own baseline is the only honest comparison |

- **Each range is attributed to the body that publishes it, and only ranges that exist are sent.** WHO publishes the two it is named for here; it publishes no resting heart rate or sleep duration range, so those carry their actual source rather than being re-labelled WHO. `steps` gets none because no standards body publishes a daily step count — WHO's physical activity guidelines are written in minutes of moderate activity per week, and converting those to steps would be our arithmetic under WHO's name. `temperature` gets none because skin temperature is a wearer-relative measurement, already compared against the device's own nightly baseline.
- **Age:** a CardiMember is validated as 18–120 years old, so every range here is an adult one and no paediatric band (where resting and breathing rates diverge sharply) can apply. Within adulthood only **`sleep`** carries a published age split, and it takes it — the NSF's older-adult band from 65, which most CardiMembers fall in. The other three are published as single adult bands; narrowing them per member would be our own tailoring under the publisher's name. None of the four is published split by sex.
- **Presentational only:** `reference` is never an input to `status` or alerting, both of which stay relative to the member's own baseline. CardiTrack is not a medical device, and a reading outside a population range is context for a caregiver, not a finding. The single exception is the **sleep range**, both ends of which cap the sleep `qualityScore` and can only lower it — see the star-rating notes above for why sleep alone cannot be rated on the member's own normal.

### Errors

| Status | When |
|--------|------|
| 403 | JWT valid but no local user row |
| 404 | No active, view-permitted link between the caller and this CardiMember, or member inactive |

---

## GET `/api/v1/insights/members/{id}/baseline` — implemented (AI narrative)

MedGemma-generated **narrative** analysis of a CardiMember's baseline trends — this is prose, not the numeric baseline endpoint planned below.

**Priority:** P1 | **Auth Required:** Yes

### Response `200 OK` (wrapped in `ApiResponse<T>`)

```json
{
  "cardiMemberId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "summary": "Margaret's activity has been stable over the past month...",
  "keyFindings": [
    "Resting heart rate trending slightly upward",
    "Sleep duration consistent with baseline"
  ],
  "isLearning": false,
  "isProvisional": false,
  "baselinePeriodDays": 30,
  "generatedAt": "2026-08-07T10:00:00Z"
}
```

**`isLearning`** is `true` until the member has any `PatternBaseline` — the same test the dashboard's `baseline.isLearning` makes. While it is true, the summary describes **what has been observed so far**, not how the member compares to normal, because no normal has been established yet. When only a provisional (7/14-day) baseline exists, **`isProvisional`** is `true` instead and the summary is an early impression — a comparison, not a settled pattern:

```json
{
  "summary": "So far the readings show a settled daily rhythm: activity concentrated in the late morning, and sleep starting at a consistent hour...",
  "keyFindings": ["Nine days of data captured so far"],
  "isLearning": true,
  "isProvisional": false,
  "baselinePeriodDays": null
}
```

```json
{
  "summary": "The early data shows a steady resting heart rate and consistent sleep — clearer once the full baseline is in.",
  "keyFindings": ["Resting heart rate steady so far"],
  "isLearning": false,
  "isProvisional": true,
  "baselinePeriodDays": 14
}
```

Clients must not label either output as a trend assessment. The learning prompt asks the model to call nothing unusual (the words it must not use are not listed — MedGemma would echo them). The provisional prompt treats a short window as an impression, not settled.

## GET `/api/v1/insights/members/{id}/digest` — implemented (AI narrative)

The member's **family digest**: a plain-language **rolling summary of the local day in progress**, regenerated whenever the member's readings move — with a **1-hour floor** between regenerations — widening to 2 hours early in the member's day, and not lifting at all between their bedtime and wake time — waived when samples indicate a problem, daily readings diverge from the baseline or jumped from yesterday, or an alert changed. The digest Cloud Scheduler is **half-hourly**; the assessor job re-runs generation immediately afterwards so a concerning hour is not stuck behind the next slot. It is *not* a fixed 06:00 previous-day snapshot: the text a caregiver reads at noon describes the day so far. Read-only — no model call happens on this path; `?date=YYYY-MM-DD` selects a specific local day, otherwise the most recent digest is returned.

`?audience=` selects which series to read: `family` (the default, and what every caller got before daybook entries existed), `daybook`, `weekbook` (one entry per finished week) or `monthbook` (one per finished calendar month) — each keyed by its period's **last day**. Anything else is **400**, not silently ignored — a typo'd audience returning the family summary would show a caregiver one kind of summary under the heading of another.

**Priority:** P1 | **Auth Required:** Yes

### Response `200 OK` (wrapped in `ApiResponse<T>`)

```json
{
  "cardiMemberId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "localDate": "2026-08-10",
  "audience": "Family",
  "headline": "A settled day so far",
  "text": "Margaret is having a settled day so far — activity in her usual range after a full night's sleep. Nothing needs your attention.",
  "suggestion": "A short afternoon walk together would top up her steps.",
  "urgency": "watch",
  "generatedAtUtc": "2026-08-10T14:20:12Z"
}
```

`headline` is a few words naming what the summary is about — `null` on digests generated before headlines existed, so clients fall back to their own label. `suggestion` is **one** short way the family could support the member today, generated alongside the text; it is `null` when a generation produced none that survived validation, and clients hide the section rather than render a mangled line. `urgency` is `watch` / `check-in` / `concerning` / `act-now` — the model's own read of how soon the family should act, alongside (never in place of) the dashboard's deterministic alert-driven status.

`404` when no digest has been generated yet — the first days of a new member legitimately are, since digests require a day of data behind them. `localDate` is the member's local calendar day the text describes, so clients render it without timezone arithmetic.

The prompt carries a member context block — age, sex, and caregiver-entered medical notes — because a resting heart rate is not interpretable without them. The member's **name and id are never sent** to the model.

## GET `/api/v1/insights/members/{id}/digests` — implemented

Digest **history**, newest first. `?audience=family` (the default) returns the current digest and the regenerations behind it, several of which describe the same day; `?audience=daybook` returns **one entry per finished day**, because a review is written once and never recomputed — this is what the mobile **Journal** tab lists. `?audience=weekbook` returns **one entry per finished week**, dated by the week's last day, and `?audience=monthbook` **one per finished calendar month**, dated by the month's last day. Each is written from its own period's measurements rather than from the books below it, so the series are independent — a member may legitimately have one and not another for the same date, which is why each has its own partial unique index rather than sharing one. `?limit=` caps the page (default 24; an omitted or nonsense value takes the default, and the service clamps into range).

Four optional filters, all applied **before** `limit` — a search that only read the first page would answer "not found" about a review it never looked at: `?search=` matches case-insensitively over the text, headline and suggestion (LIKE wildcards in the caregiver's own term are escaped, so searching `100%` finds the string); `?from=` / `?to=` bound `localDate` inclusively (**400** when `from` is after `to`); `?urgency=` keeps one tier, in the wire vocabulary (`watch` / `check-in` / `concerning` / `act-now`) — an unrecognised value is **400**, not silently ignored, the same stance the alert filters take on a typo'd severity. Returns an **empty list rather than 404** when the member has no digests yet — "no summaries yet" is an ordinary answer to a history question, where the single-digest endpoint above is asking for a thing that either exists or does not.

**Priority:** P1 | **Auth Required:** Yes

## GET `/api/v1/insights/members/{id}/status` — implemented (AI narrative)

A short, empathetic line describing the member's current status — what the dashboard's hero card shows once it resolves, in place of the fixed per-severity-tier copy the client renders while the call is in flight. Cached per member; a `null` message means there is nothing to say yet and the client keeps its existing copy.

**Priority:** P1 | **Auth Required:** Yes

---

## GET `/api/v1/cardimembers/{id}/health/summary`

> **Planned — not yet implemented.** The dashboard endpoint above is the current source for daily metrics.

Get the daily health overview for a single CardiMember for a specific date, including all key metrics and comparison to baseline. (Design intent: adds a `date` query parameter, an `activeMinutes` metric, and a `deviceSource` block.)

**Priority:** P0

---

## GET `/api/v1/cardimembers/{id}/health/trends`

> **Planned — not yet implemented.** Today the only time-series available is the 7-day `series` inside each dashboard metric.

Get time-series trend data for charts over `7d`/`30d`/`90d`/custom ranges, with per-day alert annotations.

**Priority:** P1

---

## GET `/api/v1/cardimembers/{id}/health/baseline`

> **Planned — not yet implemented.** The dashboard's `baseline` block covers learning progress today; the AI narrative version exists at `GET /api/v1/insights/members/{id}/baseline` (above) but returns prose, not numbers.

Get the current calculated numeric baseline values (steps, resting heart rate, sleep, typical wake/sleep times) and learning progress.

**Priority:** P1

---

## POST `/api/v1/cardimembers/{id}/health-data/batch`

> **Planned — not yet implemented.** No Apple Health bridge exists — there is no ingestion endpoint. Server-OAuth data arrives **notify-then-fetch** via the Google Health webhook (a provider notification triggers a targeted sync), with the Worker's 10-minute poll as the fallback — see [devices.md](devices.md).

Device-bridge ingestion for **on-device providers** (Apple Health): the mobile app would read HealthKit locally and upload normalized daily samples.

**Priority:** P1 (ships with Apple Watch support — see [release matrix](../../../release_matrix.md))

---

## GET `/api/v1/cardimembers/{id}/health/export`

> **Planned — not yet implemented.** No FHIR/HL7/PDF/CSV export code exists (only unused DTO fields on the reports request). The nearest current capability is the LLM-generated text report — see [reports.md](reports.md).

Export health data in human-readable (PDF, CSV) and interoperable medical formats (FHIR R4, HL7 v2) for doctor visits and EHR integration.

**Priority:** P0 (PDF, CSV, FHIR R4 in MVP 1) | P1 (HL7 v2 added in MVP 2)

---

**Related:** [readme.md](readme.md) | [alerts.md](alerts.md) | [reports.md](reports.md) | [devices.md](devices.md) | [User Stories 2.1, 2.2, 2.3, 5.2, 10.1](../../ui/mobile/user_stories.md)

**Last Updated:** August 14, 2026
