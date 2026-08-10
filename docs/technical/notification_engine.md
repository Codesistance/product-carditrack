# Notification Engine — Data-Completeness Alerts & Value Nudges

> **Status: Design.** Nothing in this document is built. It is the plan for the engine that tells a
> caregiver *"CardiTrack is missing X — here is what filling it in gets you"*, and lets them either
> comply or silence it.

**Scope:** the non-clinical notification stream — data gaps, capability unlocks, account state.
**Out of scope:** health anomaly alerts (the five statistical `AlertType` values and the AI pipeline's
`long_term_trend`). Those stay in `Alert` / the GCP pipeline. §2 defines the boundary and §9 defines
how the two streams share one delivery path without double-notifying.

**Related:** [user_onboarding_process.md](./user_onboarding_process.md) · [data_sync_architecture.md](./data_sync_architecture.md) · [llm_design.md](../llm_design.md) · [notifications API](../execution/backend/api/notifications.md) · [alerts API](../execution/backend/api/alerts.md) · [release_matrix.md](../release_matrix.md)

---

## 1. The problem

CardiTrack's value is entirely downstream of data the user has to supply. Today the system fails
silently when that data is missing:

| Missing input | What silently never happens | Detectable from |
|---|---|---|
| No device connected | Nothing at all works | no active `DeviceConnections` row |
| Sleep scope not granted | `AlertType.Sleep` can never fire; sleep baseline never forms | `DeviceConnection.Scopes` JSON |
| Watch not worn overnight | Same — `BaselineCalculator` needs **7 samples per metric** | `DeviceActivityLog.SleepMinutes` null rate |
| `User.TimeZoneId` left at `"UTC"` | Quiet hours, digests, and "no morning activity by 11am" all land at the wrong hour | `Users.TimeZoneId` |
| No emergency contact | The red-alert one-tap call (M1-12) has nothing to dial | `CardiMembers.EmergencyContact*` |
| `ReceiveAlerts` off for every caregiver | Member is monitored but **nobody is told** | `UserCardiMembers` |
| Token expired / auth error | Monitoring is down and looks identical to "everything is fine" | `ConnectionStatus` |

A family member's greatest fear is silence ([llm_design.md](../llm_design.md#device-check)). Every row
above produces exactly that. The engine's job is to convert each into a **specific, actionable,
benefit-framed prompt** — and, because nagging is its own failure mode, to make silencing it a
first-class, respected outcome.

**Primary KPI: comply rate per rule** (nudges shown → gap closed within 14 days). A rule below ~15%
is nagging, not helping, and gets reworked or deleted (§11).

---

## 2. Two streams, one pipe

| | **Health alerts** (`Alert`) | **Notifications** (this engine) |
|---|---|---|
| Subject | The monitored person's body | The account's data completeness |
| Produced by | Statistical rules (R1) / GCP AI pipeline (R2) | `CardiTrack.Worker` — non-AI, DB polling |
| Urgency | Minutes | Hours to weeks |
| Lifecycle | New → Acknowledged → Resolved | Open → Snoozed / Dismissed / Muted → **Resolved by the user fixing it** |
| Silenceable | Sensitivity tuning only | Yes — snooze, dismiss, mute (§6) |
| Out-of-app channel | Push, immediately | In-app first; email digest; **push only for the two safety rules** |

They are **separate tables**. Overloading `Alert` would drag acknowledgment semantics, `CardiMemberId`
(nudges are often user-scoped, not member-scoped), and severity colours onto a model that needs none of
them — and would put profile-completeness rows into the caregiver's clinical alert list.

They **share** one delivery path: preferences, quiet hours, channel fan-out, the outbox, and push
tokens are built once here and reused by alert delivery (§9).

### Placement (binding rules from `CLAUDE.md`)

- Detection is a **non-AI background job doing DB polling** → it lives in `CardiTrack.Worker`, full stop.
  It may not go on Cloud Run alongside the AI pipeline.
- The GCP pipeline stays AI-only; it *feeds* the outbox through an authenticated internal endpoint
  rather than growing its own preference/quiet-hours logic (§9).
- `CronBackgroundService`, `WorkerOptions`, `WorkerServiceExtensions` stay Worker-private; the new
  workers subclass them exactly as the existing five do.

```
CardiTrack.Domain          Notification, NotificationDelivery, PushDeviceToken,
                           UserNotificationPreference, NotificationRuleMute + enums
CardiTrack.Application     INudgeRule catalogue (pure), NudgeContext, NudgeReconciler,
                           NotificationService, DeliveryPlanner (quiet hours/budget)
CardiTrack.Infrastructure  EF configs + migrations, repositories, channel adapters
                           (FCM, APNs, email), NotificationSnapshotQueries (set-based)
CardiTrack.Worker          DataCompletenessWorker, NotificationDispatchWorker,
                           NotificationDigestWorker, NotificationRetentionWorker
CardiTrack.API             /api/v1/notifications/* (inbox, actions, prefs, devices,
                           internal enqueue)
CardiTrack.Mobile / .Web   Inbox screen, dashboard "Complete the picture" card,
                           notification settings screen
```

---

## 3. Rule catalogue

Each rule declares: **detection**, **the capability it unlocks** (the copy the user actually reads),
**where tapping it lands**, **priority**, **silence policy**, and **auto-resolve condition**.

Silence policy: `Full` = snooze + dismiss + mute-forever · `Snooze` = time-boxed only, re-arms ·
`Safety` = cannot be muted; max snooze 72h, and dismissing records an explicit acknowledgement (§6).

### Safety — monitoring is degraded or nobody is listening

| Code | Detection | Benefit copy (what it unlocks) | Priority | Silence |
|---|---|---|---|---|
| `DEVICE_AUTH_BROKEN` | `ConnectionStatus` ∈ {`TokenExpired`, `AuthError`} | "Reconnect to restore monitoring — no data is reaching CardiTrack right now." | Critical | Safety |
| `NO_ALERT_RECIPIENT` | Every active `UserCardiMember` for the member has `ReceiveAlerts = false`, **or** every recipient has all channels off | "Nobody is set to receive {Name}'s alerts. Turn one on so a red alert reaches someone." | Critical | Safety |

These two are the only rules permitted to push (§9). Everything below is in-app + digest.

### Blocking — core value is unavailable

| Code | Detection | Benefit copy | Priority | Silence | Auto-resolve |
|---|---|---|---|---|---|
| `NO_DEVICE_CONNECTED` | No active `DeviceConnection` for a member ≥24h old | "Connect a wearable to start seeing {Name}'s daily activity, heart rate and sleep." | Critical | Snooze (7d) | Connection created |
| `DEVICE_STALE_LONG` | `LastSyncDate` > 48h (see §9 dedup vs the pipeline's 2h device-check) | "{Name}'s watch hasn't synced in two days. A charge or a phone-app open usually fixes it." | High | Snooze (3d) | Successful sync |
| `TIMEZONE_DEFAULT` | `Users.TimeZoneId = "UTC"` **and** `Locale` implies otherwise | "Set your time zone so quiet hours, the morning digest and 'no activity yet today' use *your* clock." | High | Snooze (30d) | Non-UTC tz saved |
| `BASELINE_STALLED` | `daysCaptured` unchanged 7 days and < 80% coverage gate | "{Name} is {n}/30 days into learning. Alerts switch on once the picture is complete." | High | Snooze (14d) | Coverage advances |
| `PUSH_UNREACHABLE` | Push preference on but no active `PushDeviceToken`, or last token rejected `NotRegistered` | "Push is on but this phone isn't registered — urgent alerts can't reach you." | High | Snooze (7d) | Token registered |

### Capability unlocks — "submit this, get that"

| Code | Detection | Benefit copy | Priority | Silence | Auto-resolve |
|---|---|---|---|---|---|
| `SLEEP_SCOPE_MISSING` | `Scopes` lacks the sleep bundle | "Grant sleep access to unlock sleep-disruption alerts and the nightly summary." | High | Full | Scope present |
| `SLEEP_DATA_SPARSE` | `SleepMinutes` null on ≥7 of last 14 days, scope present | "Worn overnight 7 nights, CardiTrack can spot sleep changes. {n}/7 so far." | Medium | Full | ≥7 samples |
| `EMERGENCY_CONTACT_MISSING` | `EmergencyContactName` or `…Phone` null | "Add an emergency contact and red alerts get a one-tap call button." | High | Full | Both set |
| `NO_PRIMARY_CAREGIVER` | No `IsPrimaryCaregiver` among active links | "Name a primary caregiver so urgent alerts have a clear first responder." | Medium | Full | Flag set |
| `DOB_MISSING` | `DateOfBirth` = `default` (see §12 — schema gap) | "{Name}'s age lets us use age-appropriate heart-rate thresholds instead of generic ones." | Medium | Full | Plausible DOB |
| `MEDICAL_NOTES_EMPTY` | `MedicalNotes` null/empty | "Conditions and medications make AI insights and the doctor-visit report far more specific. Encrypted at rest, visible only to your family." | Low | Full | Non-empty |
| `MEMBER_CONTACT_MISSING` | `CardiMember.Phone` null | "Add {Name}'s number to call or text straight from an alert." | Low | Full | Set |

### Account & lifecycle

| Code | Detection | Benefit copy | Priority | Silence | Wave |
|---|---|---|---|---|---|
| `MONITORING_PAUSE_ENDED` | `MonitoringPausedUntil` elapsed < 24h ago | "Monitoring for {Name} has resumed." (informational) | Low | Full | R1 |
| `PAUSE_LEFT_LONG` | Paused > 14 days | "{Name} has been paused for two weeks — resume, or extend deliberately." | Medium | Snooze (7d) | R1 |
| `TRIAL_EXPIRING` | `TrialEndDate` within 7/3/1 days | "Your trial ends in {n} days. Add a card to keep alerts running." | High | Snooze | R2 |
| `CONSENT_NOT_RECORDED` | Per-metric consent row absent | "Confirm which metrics your family may see." | High | Safety | R1 (with consent feature) |

**Rule authoring rules.** Copy is *benefit-first, guilt-free* — name the capability, never "you failed
to". No raw metric values in the body (llm_design §privacy). One rule = one gap = one action; if the
fix needs two screens, it is two rules. Every rule ships with a deep link to the exact field, not to a
settings root.

---

## 4. Data model

```
Notification                            -- one open gap, per target user
├── Id, OrganizationId, UserId, CardiMemberId?          (nullable: some gaps are account-level)
├── RuleCode        string(64)          -- catalogue key, e.g. "SLEEP_SCOPE_MISSING"
├── RuleVersion     int                 -- bumped when detection or copy materially changes
├── Category        enum                -- Safety | Blocking | Unlock | Account
├── Priority        enum                -- Critical | High | Medium | Low
├── Fingerprint     string(128) UNIQUE  -- SHA256(RuleCode|UserId|ScopeId|Discriminator)
├── TitleKey / BodyKey / BenefitKey     -- localization keys, not baked strings
├── TemplateData    jsonb               -- {"name":"Margaret","n":4}  (no PHI values)
├── ActionDeepLink  string(256)         -- carditrack://cardimembers/{id}/edit#emergencyContact
├── State           enum                -- Open | Snoozed | Dismissed | Resolved | Superseded
├── SnoozedUntil, DismissedDate, ResolvedDate, ResolutionReason
├── FirstDetectedDate, LastEvaluatedDate, FirstSeenDate
└── IsActive, CreatedDate, UpdatedDate                  -- BaseEntity + ISoftDeletable

NotificationDelivery                    -- transactional outbox, one row per channel attempt
├── NotificationId, Channel (InApp|Push|Email|Sms)
├── State (Pending|Scheduled|Sent|Suppressed|Failed|DeadLettered)
├── ScheduledFor    -- quiet-hours/local-time deferral lands here, not in a sleep loop
├── DedupKey UNIQUE -- shared namespace with Alert delivery (§9)
├── Attempts, NextAttemptAt, LastError, ProviderMessageId, SentDate

UserNotificationPreference              -- one row per user; supersedes the JSON blob (§12)
├── UserId UNIQUE
├── PushEnabled, EmailEnabled, SmsEnabled
├── QuietHoursStart/End (TimeOnly?), QuietHoursTimeZoneId  -- resolved from User.TimeZoneId
├── DigestEnabled, DigestDayOfWeek, DigestTimeLocal
└── MutedCategories (jsonb string[])

NotificationRuleMute                    -- the "don't ask again" record
├── UserId, RuleCode, CardiMemberId?    -- UNIQUE(UserId, RuleCode, CardiMemberId)
├── MutedDate, MutedUntil?              -- null = forever
└── AcknowledgedConsequence bool        -- true only for Safety-class dismissals

PushDeviceToken
├── UserId, DeviceId, Platform (Ios|Android), Token, AppVersion
├── LastSeenDate, DisabledDate, DisabledReason
└── UNIQUE(UserId, DeviceId)
```

**Why `Fingerprint`:** the reconciler is idempotent because identity is content-derived, not
generated. Running the worker twice in a minute produces zero duplicates without a distributed lock.
The `Discriminator` segment is what makes a *changed* gap a new notification — e.g. sleep sparsity
carries the fortnight bucket, so "still sparse next fortnight" re-arms after a dismissal while
"still sparse tomorrow" does not.

**Localization keys, not strings.** Copy lives in resource files (the app already ships
`Mobile.Core/Localization`). Storing rendered English in the DB would make the Spanish/Mandarin plan in
the onboarding doc a data migration, and would freeze copy at detection time.

---

## 5. Evaluation — detection, reconciliation, targeting

### 5.1 Snapshot, then pure rules

```csharp
public interface INudgeRule
{
    string    RuleCode    { get; }
    int       Version     { get; }
    NudgeSpec Spec        { get; }   // category, priority, silence policy, channels
    NudgeVerdict Evaluate(NudgeContext context);   // pure: no I/O, no clock, no DbContext
}
```

`NudgeContext` is a **pre-fetched, per-member snapshot**: user, member, active connections, parsed
scopes, preference row, existing open notifications, baseline state, and *aggregate coverage counts*
(`nullSleepDays14`, `distinctDataDays30`, …) — computed set-based in SQL, never by loading
`ActivityLog` rows. `utcNow` is a context field, so every rule is deterministic and table-testable.
This mirrors `BaselineProgress`, which is already a pure static over pre-fetched inputs.

### 5.2 Reconcile, don't insert

Each run computes the **desired open set** for a user and diffs it against stored state:

| Desired | Stored | Action |
|---|---|---|
| present | absent | Insert `Open`, enqueue deliveries per §7 budget |
| present | `Open` | Touch `LastEvaluatedDate`, refresh `TemplateData` (progress counters move) |
| present | `Snoozed`, expired | → `Open`, re-enqueue |
| present | `Snoozed`, live | Leave alone |
| present | `Dismissed` | Leave dismissed unless `RuleVersion` increased **or** the discriminator changed |
| present | muted (`NotificationRuleMute`) | Skip entirely — not even in-app |
| absent | `Open`/`Snoozed` | → `Resolved`, `ResolutionReason = GapClosed` |

**The user never has to dismiss a nudge they fixed.** Auto-resolution is the whole point of
reconciliation, and it is what keeps the inbox honest.

Member soft-deleted, org deleted, or monitoring paused → all scoped notifications go `Superseded`
(paused) or `Resolved` (deleted). Nagging about a member someone deliberately paused is the fastest
way to teach users to ignore the channel.

### 5.3 Who gets nudged

A five-caregiver family must not get five copies of "add an emergency contact".

1. **Ownable gaps** (member profile, device, scopes) → the member's **primary caregiver**; fall back
   to the earliest-assigned active caregiver with `CanViewHealthData`, then the org `Admin`.
2. **Personal gaps** (`TIMEZONE_DEFAULT`, `PUSH_UNREACHABLE`) → that user only.
3. **Org gaps** (`TRIAL_EXPIRING`) → `Admin`, or the single `Member` on a family account.
4. Everyone else sees the item **read-only** in a "the family is working on this" section — visible,
   not actionable, never delivered out-of-app. Prevents both duplicate nagging and the "I thought
   *you* did it" gap.
5. Ownership re-targets if the owner goes inactive; the fingerprint stays stable across re-targeting
   so a handover doesn't reset a dismissal.

### 5.4 Suppression windows

No nudge is created (or delivered) when:

- The account is < 48h old — except `NO_DEVICE_CONNECTED`, which *is* the onboarding path.
- The member is paused (`IsMonitoringPaused`).
- An unacknowledged **red** `Alert` is open for that member — a real event outranks housekeeping.
- The user opened the app < 10 minutes ago and the nudge is already visible in-app.
- Onboarding is genuinely incomplete (`IsOnboardingComplete = false`) — the onboarding flow owns that
  conversation; the engine picks up where it stops.

---

## 6. Comply or silence

Every notification carries exactly three affordances, and each is honoured literally.

| Affordance | Effect | Re-arm |
|---|---|---|
| **Comply** — primary action | Deep link to the exact field; on save the gap closes and reconciliation resolves it on the next pass (the API resolves it synchronously when the write is same-request — see §8) | n/a |
| **Snooze** — "not now" | `State = Snoozed`, `SnoozedUntil = now + rule default` (3–30d); user may pick 1d / 1w / 1m | Automatic at expiry |
| **Dismiss** — "don't ask again" | Writes `NotificationRuleMute` (per user + rule + member scope) | Only if `RuleVersion` increases, i.e. we changed the offer |

**Safety-class rules cannot be muted.** `DEVICE_AUTH_BROKEN`, `NO_ALERT_RECIPIENT` and
`CONSENT_NOT_RECORDED` are the cases where silence means an unmonitored person or an unlawful basis for
processing. They offer **max 72h snooze**, and the dismiss action instead opens a consequence
confirmation ("Alerts for Margaret will stay off until you turn them back on") that records
`AcknowledgedConsequence = true` and an `AuditLog` entry. This is a deliberate, logged, reversible
user choice — not an override we refuse.

**Global escape hatches**, all in one settings screen: mute a whole category; turn off all non-safety
notifications; set quiet hours; set digest cadence; **"Show me everything again"** which clears all
mutes. Every mute is visible and reversible from that screen — a silence the user can't find later is
a bug.

---

## 7. Fatigue budget

Robustness here is mostly restraint. The engine is capped, not by rule authors' good intentions, but
structurally in the `DeliveryPlanner`:

| Cap | Value |
|---|---|
| Out-of-app notifications (push/email) per user | **1 per 72h**, plus the weekly digest |
| New `Open` notifications created per user per run | 3 (surplus stays undetected until next run — priority-ordered) |
| Dashboard cards | Top 2 by priority, then recency |
| In-app inbox | Uncapped, priority-ranked, resolved items collapse |
| Push | Safety category only |
| Same rule re-notified out-of-app | ≥ 14 days apart regardless of state |

Quiet hours and digest windows are evaluated in the **user's** local time via `User.TimeZoneId` —
which is precisely why `TIMEZONE_DEFAULT` is a High-priority rule and the only in-app-only nudge shown
during the first 48h alongside device connection.

---

## 8. API surface

Extends [notifications.md](../execution/backend/api/notifications.md); same `ApiResponse<T>` envelope,
integer enums, `/api/v1` prefix, `ICardiMemberAccessService` scoping (unreadable member → **404**).

| Endpoint | Purpose |
|---|---|
| `GET /api/v1/notifications` | Inbox. Filters: `state`, `category`, `cardiMemberId`, `limit` (≤200), `offset`. Returns rendered copy for the caller's locale + `actionDeepLink` + `canMute` |
| `GET /api/v1/notifications/summary` | Badge counts + top 2 dashboard cards. One cheap call for app launch |
| `POST /api/v1/notifications/{id}/seen` | Sets `FirstSeenDate`; drives the comply-rate funnel |
| `POST /api/v1/notifications/{id}/snooze` | `{ "duration": "P7D" }`; clamped to the rule's max |
| `POST /api/v1/notifications/{id}/dismiss` | `{ "acknowledgedConsequence": true }` required for Safety rules, else **400** |
| `POST /api/v1/notifications/mutes/reset` | "Show me everything again" |
| `GET` / `PUT /api/v1/notifications/preferences` | Typed preferences (§4), partial update |
| `POST` / `DELETE /api/v1/notifications/devices` | Push token upsert / unregister — as already specced |
| `POST /api/v1/internal/notifications/enqueue` | **Service-to-service only.** Google OIDC ID token from the pipeline's service account, audience-pinned; not reachable with a user JWT |

All actions are **idempotent** and 404 on another user's notification (never 403 — same non-disclosure
convention as alerts).

**Synchronous resolution:** `CardiMemberService`, `DeviceConnectionService` and `UserService` raise a
domain event on the writes that close a gap, so tapping "Add emergency contact" and saving clears the
card before the screen pops. The worker remains the backstop, not the only path — a caregiver who
complies and still sees the nudge on the next screen learns to distrust it.

---

## 9. Delivery — one pipe, two producers

```
Detection (Worker, DB polling)  ──┐
                                  ├──> NotificationDelivery outbox ──> DispatchWorker ──> FCM / APNs
GCP AI pipeline (SeverityRouter) ─┘        (prefs, quiet hours,                        ──> Email
   POST /internal/notifications/enqueue     budget, dedup)                             ──> In-app
```

The pipeline gets a *transport*, not a copy of the rules engine — preferences, quiet hours and token
lifecycle exist once. This keeps the AI pipeline AI-only per `CLAUDE.md` while keeping the Worker free
of AI responsibilities.

**Dedup with the pipeline's device-check.** llm_design specifies a rule-based push at **2h** of
silence; this engine has `DEVICE_STALE_LONG` at **48h**. Both use `DedupKey =
device-silence:{connectionId}:{utcDate}` in a shared namespace, and `DEVICE_STALE_LONG` is suppressed
outright if a device-silence delivery already went out that day. Until the pipeline ships (R2), the
48h nudge is the only device-silence signal and carries the whole job.

**Channel adapter contract** — `INotificationChannel` with `SendAsync(delivery, ct) → Sent | Retryable |
Permanent(reason)`. Exponential backoff (1m, 5m, 30m, 2h, 12h) then `DeadLettered` with a Warning log,
matching the orphan-cleanup precedent. FCM `NotRegistered` / APNs `410` are **permanent** and disable
the token (`DisabledDate`), which in turn arms `PUSH_UNREACHABLE` — the failure feeds the engine back.

**Vendor gate:** SMS and email vendors need a **BAA** before any content naming a member ships
(onboarding doc, BAA table). Until one is signed, email is limited to a no-PHI teaser — *"CardiTrack
has 2 suggestions for your account"* + deep link — and SMS stays off. Push payloads carry a generic
title and the deep link only; the body is fetched in-app after auth.

---

## 10. Workers

Five exist today; four are added. All are `CronBackgroundService` subclasses, 6-field Cronos
expressions, configured under `Workers:{Name}` in `appsettings.json`.

| Worker | Cron | Job |
|---|---|---|
| `DataCompletenessWorker` | `0 0 6 * * *` | Evaluate + reconcile all active orgs. Batched per org, snapshot queries set-based, cancellation honoured between batches |
| `NotificationDispatchWorker` | `0 */5 * * * *` | Drain the outbox: due + not quiet-hours + within budget. Claim rows with `FOR UPDATE SKIP LOCKED` so a scaled-out Worker never double-sends |
| `NotificationDigestWorker` | `0 0 * * * *` | Hourly sweep; sends to users whose local digest hour matches now |
| `NotificationRetentionWorker` | `0 45 3 * * *` | Purge `Resolved`/`Dismissed` > 180d and delivered outbox rows > 90d; mutes are kept (they're a user preference) |

Detection runs at 06:00 UTC, *before* the 08:00-local digest window and well after the 02:30 baseline
recalculation, so a nudge about baseline progress reflects that morning's numbers.

**Concurrency & failure:** reconciliation is idempotent by fingerprint, so a crashed run simply repeats.
A `NotificationRunLog` row per run (orgs scanned, created, resolved, suppressed, duration) makes a
misfiring rule visible in one query, and lets a run resume from the last completed org.

---

## 11. Observability & the anti-nag gate

Datadog APM is already wired (`Apm:Engine`, PR #4). Emit:

- `notification.created` / `.delivered` / `.suppressed{reason}` / `.failed{channel}` — tagged `rule_code`
- `notification.seen` → `.complied` / `.snoozed` / `.dismissed` / `.muted` — the funnel
- `notification.time_to_comply` histogram per rule
- `notification.outbox_depth`, `.dead_lettered` — alert on either climbing
- Run-level: orgs scanned, wall time, rows created (spike = rule bug or bad `RuleVersion` bump)

**Review gate, enforced quarterly:** any rule with comply rate < 15% or mute rate > 30% over 500+
impressions is reworked or removed. A rule that people silence is worse than no rule — it trains users
to ignore the entire channel, including the two safety ones.

---

## 12. Existing debt this must resolve

| Issue | Resolution |
|---|---|
| `NotificationPreferencesRequest` DTO is per-**CardiMember**; notifications.md designs per-**user** | User-level typed `UserNotificationPreference` for channels/quiet hours/digest; `UserCardiMember.ReceiveAlerts` stays as the per-member routing switch |
| `UserCardiMember.NotificationPreferences` JSON blob, read by nothing | Deprecate; migrate any non-`{}` values into the typed table, then drop the column |
| The DTO's validator is registered but no endpoint consumes it | Reshape to the §8 contract or delete — a validator with no endpoint is a trap for the next reader |
| `CardiMember.DateOfBirth` is non-nullable `DateOnly`, so "missing" is indistinguishable from 0001-01-01 | Make it nullable (migration) — otherwise `DOB_MISSING` guesses. Ship the rule *after* this |
| `AlertSensitivity` stored but consumed by nothing | Unchanged here, but `NO_ALERT_RECIPIENT` should also fire when sensitivity is `Low` and every channel is off |
| `TotalSteps = 7` in `OnboardingStatusResponse` with `HasNotificationPreferences` | Wire that flag to the new preference row; otherwise onboarding can never report complete |

---

## 13. Testing

- **Per-rule table tests** (`CardiTrack.UnitTests`) — rules are pure over `NudgeContext`, so each is a
  data-driven test with no DB. Boundaries explicit: 6 vs 7 sleep samples; 47h vs 49h staleness.
- **Reconciler idempotency** — run twice over a fixed snapshot, assert zero new rows and unchanged
  timestamps except `LastEvaluatedDate`.
- **State machine** — snooze expiry re-arms; dismiss survives a run; `RuleVersion` bump re-arms;
  mute suppresses even creation; gap closed → `Resolved` without user action.
- **Targeting** — five caregivers, one nudge, correct owner; owner deactivated → re-target, same
  fingerprint.
- **Quiet hours / timezone** — a user in `America/New_York` at 23:30 local defers to the morning;
  DST transition days do not double-send or skip.
- **Outbox** (`IntegrationTests`, Testcontainers — already in the harness) — `SKIP LOCKED` claiming
  under two concurrent dispatchers sends exactly once; retryable → backoff; permanent → token disabled.
- **Suppression** — paused member, open red alert, and < 48h account each yield nothing.
- **API** — cross-tenant access returns 404; Safety dismiss without acknowledgement returns 400;
  every action idempotent under replay.

---

## 14. Delivery phases

Mapped onto [release_matrix.md](../release_matrix.md) waves.

**Phase 1 — In-app only (R1).** No vendors, no BAA, no push infrastructure; ships entirely inside
surfaces that already exist.
`Notification` + `NotificationRuleMute` + migrations · `INudgeRule` + reconciler · 8 rules
(`NO_DEVICE_CONNECTED`, `DEVICE_AUTH_BROKEN`, `NO_ALERT_RECIPIENT`, `DEVICE_STALE_LONG`,
`TIMEZONE_DEFAULT`, `EMERGENCY_CONTACT_MISSING`, `SLEEP_SCOPE_MISSING`, `BASELINE_STALLED`) ·
`DataCompletenessWorker` · inbox + action endpoints · mobile inbox screen + dashboard card + settings.
Nullable-DOB migration lands here so `DOB_MISSING` can follow.

**Phase 2 — Preferences & email digest (R2).** Typed preference table + JSON-blob migration · quiet
hours + `DeliveryPlanner` budget · outbox + `NotificationDispatchWorker` · email adapter behind the BAA
gate (no-PHI teaser until signed) · `NotificationDigestWorker` · remaining unlock rules ·
`TRIAL_EXPIRING` alongside billing.

**Phase 3 — Push (R2, with the AI pipeline).** `PushDeviceToken` + FCM/APNs adapters · device
registration endpoints · `/internal/notifications/enqueue` for `SeverityRouter` · device-silence dedup
· `PUSH_UNREACHABLE` · health alerts move onto the shared pipe.

**Phase 4 — Refinement (R3/R4).** Inline push actions (matrix R4) · consent-recording nudges · family
invitation nudges · per-rule comply-rate review loop.

---

## 15. Open decisions

1. **Does the inbox count as PHI access?** It lists member names and health-data gaps. Recommendation:
   annotate `GET /api/v1/notifications` with `AuditHealthDataAccessAttribute` and update the DPIA — it
   is cheap, and the alternative is a PHI-adjacent surface outside the audit trail.
2. **Email vendor + BAA** — blocks Phase 2 content. SendGrid/Postmark decision needed by R2 planning;
   the no-PHI teaser is the fallback, not the plan.
3. **Should the wearer (not just caregivers) receive nudges?** llm_design gives wearers their own login
   and consent controls; `SLEEP_DATA_SPARSE` is genuinely theirs to act on. Deferred to R3, when
   wearer logins exist.
4. **Rule catalogue in code or DB?** Proposed: **code**, with `RuleVersion` for re-arming. DB-driven
   rules would allow copy changes without deploys but put untested predicates in production data.
5. **Web parity** — the web app is template-stage; Phase 1 is mobile + API only. Web inbox lands with
   the web dashboard.

---

**Owner:** Engineering
**Last Updated:** August 10, 2026
