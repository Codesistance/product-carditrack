# Notification Engine — In-App Data-Completeness Alerts & Value Nudges

> **Status: Design.** Nothing in this document is built. It is the plan for the engine that tells a
> caregiver *"CardiTrack is missing X — here is what filling it in gets you"*, and lets them either
> comply or silence it.

**Delivery is in-app only.** No push, no email, no SMS, no OS-level or scheduled local
notifications. Nothing this engine produces leaves the app UI. See §3 for what that constraint buys
and what it costs.

**Scope:** the non-clinical notification stream — data gaps, capability unlocks, account state.
**Out of scope:** health anomaly alerts (the five statistical `AlertType` values and the AI pipeline's
`long_term_trend`), and any out-of-app delivery those may later need. §2 defines the boundary.

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
| `User.TimeZoneId` left at `"UTC"` | "No morning activity by 11am" and the dashboard's "today" use the wrong clock | `Users.TimeZoneId` |
| No emergency contact | The red-alert one-tap call (M1-12) has nothing to dial | `CardiMembers.EmergencyContact*` |
| `ReceiveAlerts` off for every caregiver | Member is monitored but **nobody is told** | `UserCardiMembers` |
| Token expired / auth error | Monitoring is down and looks identical to "everything is fine" | `ConnectionStatus` |

Every row above is a gap the system can detect precisely and the user can close in under a minute —
if anyone tells them. The engine's job is to convert each into a **specific, actionable,
benefit-framed prompt**, and, because nagging is its own failure mode, to make silencing it a
first-class, respected outcome.

**Primary KPI: comply rate per rule** (nudges seen → gap closed within 14 days). A rule below ~15% is
nagging, not helping, and gets reworked or deleted (§10).

---

## 2. Two streams

| | **Health alerts** (`Alert`) | **Notifications** (this engine) |
|---|---|---|
| Subject | The monitored person's body | The account's data completeness |
| Produced by | Statistical rules (R1) / GCP AI pipeline (R2) | `CardiTrack.Worker` — non-AI, DB polling |
| Urgency | Minutes | Hours to weeks |
| Lifecycle | New → Acknowledged → Resolved | Open → Snoozed / Muted → **Resolved by the user fixing it** |
| Silenceable | Sensitivity tuning only | Yes — snooze, dismiss, mute (§6) |
| Delivery | In-app; push for Critical/High is the pipeline's R2 design | **In-app only, full stop** |

They are **separate tables**. Overloading `Alert` would drag acknowledgment semantics, `CardiMemberId`
(nudges are often user-scoped, not member-scoped), and clinical severity colours onto a model that
needs none of them — and would put profile-completeness rows into the caregiver's red-alert list.

**Boundary with push.** [llm_design.md](../llm_design.md) designs FCM/APNs push for Critical/High
health events as part of the R2 GCP pipeline. That remains that project's to build and own. This
engine does not build push infrastructure, does not register device tokens, and does not depend on
either ever existing. If push later ships, whether any nudge should ride it is a fresh decision, not
an assumption baked in here.

### Placement (binding rules from `CLAUDE.md`)

- Detection is a **non-AI background job doing DB polling** → it lives in `CardiTrack.Worker`, full
  stop. It may not go on Cloud Run alongside the AI pipeline.
- The GCP pipeline stays AI-only. With no shared delivery path to feed (there is no out-of-app
  channel), the two systems are now fully decoupled — no internal enqueue endpoint, no shared outbox.
- `CronBackgroundService`, `WorkerOptions`, `WorkerServiceExtensions` stay Worker-private; the new
  worker subclasses them exactly as the existing five do.

```
CardiTrack.Domain          Notification, NotificationMute + enums
CardiTrack.Application     INudgeRule catalogue (pure), NudgeContext, NudgeReconciler,
                           NotificationService, NotificationRanker
CardiTrack.Infrastructure  EF configs + migrations, repositories,
                           NotificationSnapshotQueries (set-based)
CardiTrack.Worker          DataCompletenessWorker  (+ retention, §9)
CardiTrack.API             /api/v1/notifications/*  (inbox, actions, mutes)
CardiTrack.Mobile / .Web   Inbox screen, dashboard "Complete the picture" card,
                           notification settings screen
```

---

## 3. What in-app-only means

**It is a state surface, not an event stream.** This is the design consequence that matters most.
A pushed notification is a one-shot event — miss it and it's gone, so push systems need delivery
receipts, retry, dedup windows, and "did they see it" bookkeeping. An in-app inbox has none of that:
it is re-read from the database every time it is opened, so **whatever it shows is always current
truth**. There is no missed-notification problem to engineer around. That makes the
fingerprint-and-reconcile model in §5 not merely a nice fit but the whole mechanism — the inbox is a
projection of "which gaps are open right now," and it is correct by construction.

**What drops out of the design entirely:** the delivery outbox and dispatch worker, retry/backoff and
dead-lettering, `PushDeviceToken` and FCM/APNs adapters, per-channel preferences, quiet hours (nothing
interrupts, so there is no bad hour to protect), the weekly email digest and its worker, local-time
send scheduling and its DST edge cases, and every vendor dependency — which means **no BAA and no DPA
to negotiate**, and no PHI leaving CardiTrack's own systems. Phase 1 in the previous revision was
carved out precisely to avoid those; now it is simply the whole engine.

**What it costs, stated plainly:** a user who stops opening the app cannot be reached. For most rules
that is fine and even correct — an empty medical-notes field is not worth an interruption. For the two
safety rules it is a real limitation: if monitoring is down because a device's OAuth token broke, an
absent caregiver will not learn that until they next open the app. In-app-only leaves **prominence as
the only escalation axis**, so §7 spends it there: safety rules get a persistent, unmutable dashboard
banner and are mirrored into the member's dashboard status rather than sitting in an inbox nobody
opens. If reachability for monitoring-down ever becomes a hard requirement, that is a deliberate push
decision to make alongside the R2 pipeline work — not something this engine should half-build now.

**Also excluded:** the OS app-icon badge is a system surface. Unread counts render on the in-app tab
bar only.

---

## 4. Rule catalogue

Each rule declares **detection**, **the capability it unlocks** (the copy the user actually reads),
**where tapping it lands**, **priority**, and **silence policy**.

Silence policy: `Full` = snooze + mute-forever · `Snooze` = time-boxed only, re-arms ·
`Safety` = cannot be muted; max snooze 72h, and dismissing records an explicit acknowledgement (§6).

### Safety — monitoring is degraded or nobody is listening

| Code | Detection | Benefit copy (what it unlocks) | Priority | Silence |
|---|---|---|---|---|
| `DEVICE_AUTH_BROKEN` | `ConnectionStatus` ∈ {`TokenExpired`, `AuthError`} | "Reconnect to restore monitoring — no data is reaching CardiTrack right now." | Critical | Safety |
| `NO_ALERT_RECIPIENT` | Every active `UserCardiMember` for the member has `ReceiveAlerts = false` | "Nobody is set to receive {Name}'s alerts. Turn one on so a red alert reaches someone." | Critical | Safety |

These two get the persistent banner treatment in §7; they are the only rules that do.

### Blocking — core value is unavailable

| Code | Detection | Benefit copy | Priority | Silence | Auto-resolve |
|---|---|---|---|---|---|
| `NO_DEVICE_CONNECTED` | No active `DeviceConnection` for a member ≥24h old | "Connect a wearable to start seeing {Name}'s daily activity, heart rate and sleep." | Critical | Snooze (7d) | Connection created |
| `DEVICE_STALE_LONG` | `LastSyncDate` > 48h | "{Name}'s watch hasn't synced in two days. A charge or a phone-app open usually fixes it." | High | Snooze (3d) | Successful sync |
| `TIMEZONE_DEFAULT` | `Users.TimeZoneId = "UTC"` **and** `Locale` implies otherwise | "Set your time zone so 'no activity yet today' and daily summaries use *your* clock, not UTC." | High | Snooze (30d) | Non-UTC tz saved |
| `BASELINE_STALLED` | `daysCaptured` unchanged 7 days and < 80% coverage gate | "{Name} is {n}/30 days into learning. Alerts switch on once the picture is complete." | High | Snooze (14d) | Coverage advances |

### Capability unlocks — "submit this, get that"

| Code | Detection | Benefit copy | Priority | Silence | Auto-resolve |
|---|---|---|---|---|---|
| `SLEEP_SCOPE_MISSING` | `Scopes` lacks the sleep bundle | "Grant sleep access to unlock sleep-disruption alerts and the nightly summary." | High | Full | Scope present |
| `SLEEP_DATA_SPARSE` | `SleepMinutes` null on ≥7 of last 14 days, scope present | "Worn overnight 7 nights, CardiTrack can spot sleep changes. {n}/7 so far." | Medium | Full | ≥7 samples |
| `EMERGENCY_CONTACT_MISSING` | `EmergencyContactName` or `…Phone` null | "Add an emergency contact and red alerts get a one-tap call button." | High | Full | Both set |
| `NO_PRIMARY_CAREGIVER` | No `IsPrimaryCaregiver` among active links | "Name a primary caregiver so urgent alerts have a clear first responder." | Medium | Full | Flag set |
| `DOB_MISSING` | `DateOfBirth` = `default` (see §11 — schema gap) | "{Name}'s age lets us use age-appropriate heart-rate thresholds instead of generic ones." | Medium | Full | Plausible DOB |
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

## 5. Data model

Two tables. That is the whole persistent footprint.

```
Notification                            -- one open gap, per target user
├── Id, OrganizationId, UserId, CardiMemberId?          (nullable: some gaps are account-level)
├── RuleCode        string(64)          -- catalogue key, e.g. "SLEEP_SCOPE_MISSING"
├── RuleVersion     int                 -- bumped when detection or copy materially changes
├── Category        enum                -- Safety | Blocking | Unlock | Account
├── Priority        enum                -- Critical | High | Medium | Low
├── Fingerprint     string(128) UNIQUE  -- SHA256(RuleCode|UserId|ScopeId|Discriminator)
├── TitleKey / BodyKey / BenefitKey     -- localization keys, not baked strings
├── TemplateData    jsonb               -- {"name":"Margaret","n":4}  (no metric values)
├── ActionDeepLink  string(256)         -- carditrack://cardimembers/{id}/edit#emergencyContact
├── State           enum                -- Open | Snoozed | Resolved | Superseded
├── SnoozedUntil, ResolvedDate, ResolutionReason
├── FirstDetectedDate, LastEvaluatedDate, FirstSeenDate
├── IsOwner         bool                -- false = read-only "family is working on this" (§5.3)
└── IsActive, CreatedDate, UpdatedDate                  -- BaseEntity + ISoftDeletable

NotificationMute                        -- the "don't ask again" record; also the whole
├── UserId                                 preference surface (§6)
├── RuleCode?  string(64)               -- one of RuleCode / Category is set
├── Category?  enum
├── CardiMemberId?                      -- UNIQUE(UserId, RuleCode, Category, CardiMemberId)
├── MutedDate, MutedUntil?              -- null = forever
└── AcknowledgedConsequence bool        -- true only for Safety-class dismissals
```

There is no `Dismissed` state: dismissing writes a `NotificationMute` and resolves the row. Keeping a
dismissed-but-present row would mean two places encode "don't show this," which is exactly the kind of
disagreement `AlertStatus` avoids by being derived rather than stored.

**No preference table.** With no channels, no quiet hours and no digest, the only thing a user can
configure is what to silence — and that is `NotificationMute`. The R2 release-matrix row *"Notification
preferences (global + per-member, quiet hours, sensitivity)"* belongs to **health alert** delivery and
stays with that work; this engine does not own it and should not pre-build it.

**Why `Fingerprint`:** the reconciler is idempotent because identity is content-derived, not
generated. Running the worker twice in a minute produces zero duplicates without a distributed lock.
The `Discriminator` segment is what makes a *changed* gap a new notification — e.g. sleep sparsity
carries the fortnight bucket, so "still sparse next fortnight" re-arms after a mute expiry while
"still sparse tomorrow" does not.

**Localization keys, not strings.** Copy lives in resource files (the app already ships
`Mobile.Core/Localization`). Storing rendered English in the DB would make the Spanish/Mandarin plan in
the onboarding doc a data migration, and would freeze copy at detection time.

---

## 6. Evaluation — detection, reconciliation, targeting

### 6.1 Snapshot, then pure rules

```csharp
public interface INudgeRule
{
    string    RuleCode    { get; }
    int       Version     { get; }
    NudgeSpec Spec        { get; }   // category, priority, silence policy
    NudgeVerdict Evaluate(NudgeContext context);   // pure: no I/O, no clock, no DbContext
}
```

`NudgeContext` is a **pre-fetched, per-member snapshot**: user, member, active connections, parsed
scopes, existing open notifications, active mutes, baseline state, and *aggregate coverage counts*
(`nullSleepDays14`, `distinctDataDays30`, …) — computed set-based in SQL, never by loading
`ActivityLog` rows. `utcNow` is a context field, so every rule is deterministic and table-testable.
This mirrors `BaselineProgress`, which is already a pure static over pre-fetched inputs.

### 6.2 Reconcile, don't insert

Each run computes the **desired open set** for a user and diffs it against stored state:

| Desired | Stored | Action |
|---|---|---|
| present | absent | Insert `Open` |
| present | `Open` | Touch `LastEvaluatedDate`, refresh `TemplateData` (progress counters move) |
| present | `Snoozed`, expired | → `Open` |
| present | `Snoozed`, live | Leave alone |
| present | muted | Skip entirely — no row created |
| absent | `Open`/`Snoozed` | → `Resolved`, `ResolutionReason = GapClosed` |

**The user never has to dismiss a nudge they fixed.** Auto-resolution is the whole point of
reconciliation, and it is what keeps the inbox honest.

Member soft-deleted, org deleted, or monitoring paused → all scoped notifications go `Superseded`
(paused) or `Resolved` (deleted). Nagging about a member someone deliberately paused is the fastest
way to teach users to ignore the surface.

### 6.3 Who gets nudged

A five-caregiver family must not get five copies of "add an emergency contact".

1. **Ownable gaps** (member profile, device, scopes) → the member's **primary caregiver**; fall back
   to the earliest-assigned active caregiver with `CanViewHealthData`, then the org `Admin`.
2. **Personal gaps** (`TIMEZONE_DEFAULT`) → that user only.
3. **Org gaps** (`TRIAL_EXPIRING`) → `Admin`, or the single `Member` on a family account.
4. Everyone else gets the row with `IsOwner = false`: visible in a "the family is working on this"
   section, never on their dashboard, no action buttons. Prevents both duplicate nagging and the
   "I thought *you* did it" gap.
5. Ownership re-targets if the owner goes inactive; the fingerprint stays stable across re-targeting
   so a handover doesn't reset a snooze.

### 6.4 Suppression windows

No nudge is created when:

- The account is < 48h old — except `NO_DEVICE_CONNECTED`, which *is* the onboarding path.
- The member is paused (`IsMonitoringPaused`).
- An unacknowledged **red** `Alert` is open for that member — a real event outranks housekeeping.
- Onboarding is genuinely incomplete (`IsOnboardingComplete = false`) — the onboarding flow owns that
  conversation; the engine picks up where it stops.

---

## 7. Prominence, comply and silence

With one channel, **prominence is the only escalation axis** — so it is rationed deliberately.

| Surface | What appears | Cap |
|---|---|---|
| **Persistent dashboard banner** | Safety category only, unmutable, no dismiss affordance; also mirrored into the member's dashboard status so a broken connection reads as broken, not as "no data" | Whatever is open |
| **Dashboard card** ("Complete the picture") | Top 2 open owned nudges by priority, then recency | 2 |
| **Inbox tab badge** | Count of unseen owned nudges (in-app tab bar, never the OS icon) | — |
| **Inbox** | All owned nudges, priority-ranked; read-only family items in a separate section; resolved items collapse | Uncapped |
| New `Open` rows created per user per run | Surplus stays undetected until the next run, priority-ordered | 3 |

The per-run creation cap is the fatigue control that survives from the multi-channel design: without
it, a newly-authored rule set can dump a dozen cards on a user the morning it ships.

### The three affordances

Every notification carries exactly three, and each is honoured literally.

| Affordance | Effect | Re-arm |
|---|---|---|
| **Comply** — primary action | Deep link to the exact field; on save the gap closes and the row resolves (synchronously — §8) | n/a |
| **Snooze** — "not now" | `State = Snoozed`, `SnoozedUntil = now + rule default` (3–30d); user may pick 1d / 1w / 1m | Automatic at expiry |
| **Dismiss** — "don't ask again" | Writes `NotificationMute` (user + rule + member scope), resolves the row | Only if `RuleVersion` increases, i.e. we changed the offer |

**Safety-class rules cannot be muted.** `DEVICE_AUTH_BROKEN`, `NO_ALERT_RECIPIENT` and
`CONSENT_NOT_RECORDED` are the cases where silence means an unmonitored person or an unlawful basis
for processing. They offer **max 72h snooze**, and the dismiss action instead opens a consequence
confirmation ("Alerts for Margaret will stay off until you turn them back on") that records
`AcknowledgedConsequence = true` and an `AuditLog` entry. This is a deliberate, logged, reversible
user choice — not an override we refuse.

**Global escape hatches**, all in one settings screen: mute a whole category; turn off all non-safety
notifications; **"Show me everything again"** which clears all mutes. Every mute is listed and
reversible there — a silence the user can't find later is a bug.

---

## 8. API surface

Extends [notifications.md](../execution/backend/api/notifications.md); same `ApiResponse<T>` envelope,
integer enums, `/api/v1` prefix, `ICardiMemberAccessService` scoping (unreadable member → **404**).

| Endpoint | Purpose |
|---|---|
| `GET /api/v1/notifications` | Inbox. Filters: `state`, `category`, `cardiMemberId`, `owned`, `limit` (≤200), `offset`. Returns rendered copy for the caller's locale + `actionDeepLink` + `canMute` |
| `GET /api/v1/notifications/summary` | Unseen count, safety banners, top 2 dashboard cards. One cheap call on app launch and dashboard refresh |
| `POST /api/v1/notifications/{id}/seen` | Sets `FirstSeenDate`; drives the comply-rate funnel and clears the badge |
| `POST /api/v1/notifications/{id}/snooze` | `{ "duration": "P7D" }`; clamped to the rule's max |
| `POST /api/v1/notifications/{id}/dismiss` | `{ "acknowledgedConsequence": true }` required for Safety rules, else **400** |
| `GET /api/v1/notifications/mutes` | List active mutes for the settings screen |
| `DELETE /api/v1/notifications/mutes/{id}` · `POST /api/v1/notifications/mutes/reset` | Un-mute one · "Show me everything again" |

The push-token endpoints specced in `notifications.md` (`POST`/`DELETE /notifications/devices`) are
**not part of this engine** — they belong with whatever eventually ships push for health alerts.

All actions are **idempotent** and 404 on another user's notification (never 403 — same
non-disclosure convention as alerts).

**Synchronous resolution:** `CardiMemberService`, `DeviceConnectionService` and `UserService` raise a
domain event on the writes that close a gap, so tapping "Add emergency contact" and saving clears the
card before the screen pops. The worker remains the backstop, not the only path — a caregiver who
complies and still sees the nudge on the next screen learns to distrust the surface. This matters more
here than it would with push: the inbox is the *only* surface, so it must never be stale.

---

## 9. Workers

Five exist today; one is added.

| Worker | Cron | Job |
|---|---|---|
| `DataCompletenessWorker` | `0 0 6 * * *` | Evaluate + reconcile all active orgs. Batched per org, snapshot queries set-based, cancellation honoured between batches |

Detection runs at 06:00 UTC — after the 02:30 baseline recalculation, so a nudge about baseline
progress reflects that morning's numbers, and before the working day in the launch regions.

`CronBackgroundService` subclass, 6-field Cronos expression, configured under
`Workers:DataCompletenessWorker` in `appsettings.json`, exactly like the existing five.

**Retention** — purge `Resolved`/`Superseded` rows older than 180 days; mutes are kept, being a user
preference. Rather than a fifth cron for two DELETE statements, this folds into the
retention/cleanup worker already listed as planned in the onboarding doc. If that worker still
doesn't exist when Phase 2 lands, add the sweep to `PartitionMaintenanceWorker`, which already owns
retention windows.

**Concurrency & failure:** reconciliation is idempotent by fingerprint, so a crashed run simply
repeats. A `NotificationRunLog` row per run (orgs scanned, created, resolved, suppressed, duration)
makes a misfiring rule visible in one query, and lets a run resume from the last completed org.

---

## 10. Observability & the anti-nag gate

Datadog APM is already wired (`Apm:Engine`, PR #4). Emit:

- `notification.created` / `.suppressed{reason}` / `.resolved{reason}` — tagged `rule_code`
- `notification.seen` → `.complied` / `.snoozed` / `.muted` — the funnel
- `notification.time_to_comply` histogram per rule
- Run-level: orgs scanned, wall time, rows created (a spike means a rule bug or a bad `RuleVersion`
  bump)

Note the funnel's denominator is `seen`, not `created` — with no push, a nudge nobody opened the app to
read tells you nothing about the rule's quality, and mixing the two would make every rule look broken.
Track `created → seen` separately as an *engagement* measure of the surface itself.

**Review gate, enforced quarterly:** any rule with comply rate < 15% or mute rate > 30% over 500+
impressions is reworked or removed. A rule people silence is worse than no rule — it trains users to
ignore the whole surface, including the safety banners.

---

## 11. Existing debt this must resolve

| Issue | Resolution |
|---|---|
| `NotificationPreferencesRequest` DTO is per-CardiMember and channel-shaped (`receiveSmsAlerts`, `receiveEmailAlerts`, `receivePushAlerts`); its validator is registered but no endpoint consumes it | Out of scope for an in-app engine and misleading to leave lying around. **Delete both**, and let the R2 alerts work introduce the shape it actually needs |
| `UserCardiMember.NotificationPreferences` JSON blob (`{sms,email,push}`), read by nothing | Leave in place, untouched — it belongs to the health-alert routing story (R2), not here. This engine must not adopt it |
| `CardiMember.DateOfBirth` is non-nullable `DateOnly`, so "missing" is indistinguishable from 0001-01-01 | Make it nullable (migration) — otherwise `DOB_MISSING` guesses. Ship the rule *after* this |
| `OnboardingStatusResponse.HasNotificationPreferences` with `TotalSteps = 7` | With no channel preferences to set, this step has nothing to satisfy it and onboarding can never report complete. Either drop the step (→ `TotalSteps = 6`) or repoint it at the R2 alert-preferences work. **Needs a product call** (§14) |
| `AlertSensitivity` stored but consumed by nothing | Unchanged here; noted so `NO_ALERT_RECIPIENT`'s detection isn't later confused with sensitivity tuning |

---

## 12. Testing

- **Per-rule table tests** (`CardiTrack.UnitTests`) — rules are pure over `NudgeContext`, so each is a
  data-driven test with no DB. Boundaries explicit: 6 vs 7 sleep samples; 47h vs 49h staleness.
- **Reconciler idempotency** — run twice over a fixed snapshot, assert zero new rows and unchanged
  timestamps except `LastEvaluatedDate`.
- **State machine** — snooze expiry re-arms; `RuleVersion` bump re-arms a muted rule; mute suppresses
  creation entirely; gap closed → `Resolved` without user action.
- **Targeting** — five caregivers, one owned nudge and four `IsOwner = false`; owner deactivated →
  re-target, same fingerprint, snooze preserved.
- **Suppression** — paused member, open red alert, and < 48h account each yield nothing.
- **Ranking & caps** (`IntegrationTests`, Testcontainers — already in the harness) — 10 eligible gaps
  yield 3 rows and 2 cards, correctly ordered; unseen count matches the inbox.
- **Synchronous resolution** — saving an emergency contact clears the row in the same request, and the
  next worker run is a no-op rather than a re-create.
- **API** — cross-tenant access returns 404; Safety dismiss without acknowledgement returns 400;
  every action idempotent under replay.

---

## 13. Delivery phases

Mapped onto [release_matrix.md](../release_matrix.md) waves. With no vendor, no BAA and no push
infrastructure in the critical path, **the engine ships whole in R1** — the phasing below is about
sequencing risk, not unblocking dependencies.

**Phase 1 — Engine + safety and blocking rules (R1).**
`Notification` + `NotificationMute` + migrations · `INudgeRule` + reconciler + snapshot queries ·
6 rules (`DEVICE_AUTH_BROKEN`, `NO_ALERT_RECIPIENT`, `NO_DEVICE_CONNECTED`, `DEVICE_STALE_LONG`,
`TIMEZONE_DEFAULT`, `BASELINE_STALLED`) · `DataCompletenessWorker` · inbox + summary + action
endpoints · mobile inbox screen, dashboard card, safety banner, settings screen. Nullable-DOB
migration lands here so `DOB_MISSING` can follow.

**Phase 2 — Unlock rules and refinement (R1).**
Remaining unlock and lifecycle rules · synchronous domain-event resolution on the three services ·
retention sweep · comply-rate dashboards and the first review pass.

**Phase 3 — Later waves.** `TRIAL_EXPIRING` with billing (R2) · `CONSENT_NOT_RECORDED` with
per-metric consent · web inbox parity when the web dashboard lands · wearer-facing nudges (R3, when
wearer logins exist).

---

## 14. Open decisions

1. **Does the inbox count as PHI access?** It lists member names against health-data gaps.
   Recommendation: annotate `GET /api/v1/notifications` with `AuditHealthDataAccessAttribute` and
   update the DPIA — cheap, and the alternative is a PHI-adjacent surface outside the audit trail.
2. **`HasNotificationPreferences` in the onboarding step count** (§11) — drop the step or repoint it.
   Product call; blocks nothing but leaves onboarding unable to report complete until answered.
3. **Should any nudge ever escape the app?** Currently no, by decision. The one case with a genuine
   argument is `DEVICE_AUTH_BROKEN` — monitoring silently down while the caregiver is away. Worth
   revisiting *only* when the R2 pipeline brings push anyway; not worth building a channel for.
4. **Rule catalogue in code or DB?** Proposed: **code**, with `RuleVersion` for re-arming. DB-driven
   rules would allow copy changes without deploys but put untested predicates in production data.
5. **Web parity** — the web app is template-stage; Phases 1–2 are mobile + API only.

---

**Owner:** Engineering
**Last Updated:** August 10, 2026
