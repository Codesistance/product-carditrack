# Notification Engine — Reliable Alert Delivery

> **Status: Phase 1 and Phase 3 shipped; brought forward ahead of R2.** Alert *generation* — listed
> below as a dependency — shipped separately in PRs #116 and #118. Firebase/FCM provisioning shipped
> in #173/#176/#177 (issue #108). The push delivery spine itself — originally scoped as R2 but pulled
> forward for provisioning lead-time reasons — shipped as one PR alongside this status update.
> **Built:** the in-app data-completeness engine (§9, §10, §12, §16 Phase 1) — `Notification`/
> `NotificationMute`/`NotificationRunLog`, the pure rule catalogue and reconciler,
> `DataCompletenessWorker`, the inbox API, mobile inbox/dashboard card/safety banners/mute
> management — **plus** the full push spine (§2–§8, §12–§13): `NotificationDelivery`/
> `PushDeviceToken`/`NotificationPreference`, the `FOR UPDATE SKIP LOCKED` outbox claimed by
> `NotificationDispatchWorker`, `FcmNotificationChannel` (FirebaseAdmin, ADC, no key file), the
> 120s/300s/900s escalation ladder, HMAC ack/fetch tokens, the internal enqueue endpoint (GoogleOidc
> scheme), `Plugin.Firebase`-based mobile registration and receipt handling, and `PushCanaryWorker`.
> **Deliberately deferred, not silently dropped** (see §17 for the full list): the iOS Notification
> Service Extension's Xcode/App-Extension project itself (its server-side dependency — the
> content-fetch endpoint — shipped; the extension target needs Mac-based verification this
> environment doesn't have); the daily active silent-push liveness probe (shipped as a passive
> "no ack signal in 7 days" disable sweep instead); the `Enqueued` counter and a precise
> `TimeToAck` histogram (blocked on the Application-layer zero-package boundary).

**Primary objective: CardiTrack reaches the caregiver, whether or not the app is open.** Everything
else in this document is subordinate to that. The engine carries two kinds of content — health alerts
and data-completeness nudges (§3) — over one delivery path built to make the first one reliable.

**Channels:** push (APNs/FCM) and in-app. **No email, no SMS** — escalation runs across *recipients
and devices*, never across vendors (§6).

**Related:** [llm_design.md](../llm_design.md) · [user_onboarding_process.md](./user_onboarding_process.md) · [data_sync_architecture.md](./data_sync_architecture.md) · [notifications API](../execution/backend/api/notifications.md) · [alerts API](../execution/backend/api/alerts.md) · [release_matrix.md](../release_matrix.md)

---

## 1. The platform reality this design is shaped by

**A mobile app cannot stay awake.** iOS suspends a backgrounded app within seconds and terminates it
under memory pressure; Android does the same through Doze and App Standby, with OEM battery managers
(Xiaomi, Huawei, Samsung) killing background work more aggressively still. This is enforced by the
platform for battery reasons — there is no entitlement, service type, or workaround that makes an app
a persistent listener:

| Mechanism | Why it can't carry alerts |
|---|---|
| iOS `BGAppRefreshTask` | Opportunistic — iOS decides when, based on usage patterns. Can be hours apart or never |
| iOS background modes (`audio`, `location`, `voip`) | Reserved for their stated purpose; App Review rejects apps that abuse them to stay resident |
| Android `WorkManager` | 15-minute floor, deferred under Doze, batched at the OS's convenience |
| Android exact alarms | `SCHEDULE_EXACT_ALARM` revoked by default since Android 13; Play policy limits it to alarm-clock and calendar apps |
| Android foreground service | Survives, at the cost of a permanent unremovable status-bar notification, Android 15's 6-hour/24h cap on `dataSync`, and OEM killers. No iOS equivalent |

**Push is the OS's answer to exactly this problem.** The system holds one persistent connection on
behalf of every installed app, so no individual app has to. A push is CardiTrack notifying the user:
it carries CardiTrack's name and icon, opens CardiTrack, and — because the payload carries no health
data (§7) — nothing meaningful passes through Apple or Google.

So the correct reading of "always awake" is: **the server is always awake, and it wakes the app.**
`CardiTrack.Worker` and the R2 GCP pipeline run 24/7 and are the things that never sleep. The app is a
renderer that gets woken, plus a full-sync-on-foreground safety net (§6.4) for anything a push missed.

---

## 2. Architecture

```
        ┌─ CardiTrack.Worker ────────────┐
        │  DataCompletenessWorker  (6am) │  gap detection, non-AI DB polling
        │  StatisticalAlertWorker  (#118)│  statistical alerts off baselines
        │  InactivityDetectionWorker     │  device silence, 2h
        │  NotificationDispatchWorker    │  outbox retry + escalation timer
        │  PushCanaryWorker        (15m) │  end-to-end delivery canary
        │  DeviceAuthRecoveryWorker (15m)│  retries broken device grants
        └──────────────┬─────────────────┘
                       │  enqueue
        ┌──────────────▼─────────────────┐        ┌──────────────────────────┐
        │  Notification + Delivery outbox │◄───────┤ GCP AI pipeline (R2)     │
        │  prefs · quiet hours · dedup    │  HTTP  │ SeverityRouter           │
        └──────────────┬─────────────────┘        └──────────────────────────┘
                       │  immediate attempt, outbox is the backstop
        ┌──────────────▼─────────────────┐
        │  FCM HTTP v1  ──► APNs (iOS)   │
        │               ──► Android      │
        └──────────────┬─────────────────┘
                       │  wakes app
        ┌──────────────▼─────────────────┐
        │  CardiTrack.Mobile              │  renders tray notification,
        │  → POST /delivered  (ack)       │  acks receipt, syncs inbox
        └────────────────────────────────┘
```

**One integration, both platforms.** FCM HTTP v1 relays to APNs for iOS, so there is one SDK
(`FirebaseAdmin`), one credential, one send path. FCM's `ApnsConfig` passes arbitrary APNs headers and
payload through, so nothing iOS-specific is lost — `interruption-level`, `apns-priority`,
`apns-collapse-id`, `apns-expiration` and critical-alert sounds are all reachable (§5). If a future
need requires bypassing FCM, the `INotificationChannel` seam (§6.2) allows a direct APNs HTTP/2
client without touching callers. Credentials live in Secret Manager, matching the deployed footprint.

**Immediate send, durable retry.** The enqueueing path attempts delivery in-process straight away —
a critical alert must not wait for a cron tick — and writes the outbox row first so a crash mid-send
loses nothing. `NotificationDispatchWorker` owns retries, escalation timers and anything the immediate
attempt failed to deliver.

> **The immediate attempt is awaited within the request or job scope — never `Task.Run`
> fire-and-forget.** Detached background work in the API would be a scheduled-job-outside-the-Worker
> violation of the CLAUDE.md rule below, and would be lost on instance shutdown besides. If awaiting
> ever costs too much request latency, the answer is to let the 30-second dispatch loop take it, not
> to detach it.

### Placement (binding rules from `CLAUDE.md`)

- Gap detection and the retry/escalation loop are **non-AI background jobs doing DB polling** →
  `CardiTrack.Worker`, full stop. Immediate dispatch from the writing path is not polling and stays
  where it is triggered.
- The GCP pipeline stays AI-only. It gets a *transport*, not a copy of the rules engine: it POSTs to
  the internal enqueue endpoint (§8) and inherits preferences, quiet hours, dedup, token lifecycle and
  escalation for free. Building a second sender there would fork all of it.
- `CronBackgroundService`, `WorkerOptions`, `WorkerServiceExtensions` stay Worker-private.

```
CardiTrack.Domain          Notification, NotificationDelivery, PushDeviceToken,
                           NotificationPreference, NotificationMute, NotificationRunLog
                           + enums.  Zero packages — persistence-ignorant entities only.

CardiTrack.Application     INudgeRule catalogue (pure), NudgeContext, NudgeReconciler,
                           NotificationService, DeliveryPlanner, EscalationPolicy.
                           Ports:
                             Interfaces/Clients/INotificationChannel        (§6.2)
                             Interfaces/Repositories/INotificationRepository
                             Interfaces/Repositories/INotificationSnapshotQueries
                             Interfaces/Services/INotificationGapResolver   (§12)
                           Zero packages — EscalationPolicy takes BCL TimeProvider,
                           and every rule is pure over NudgeContext, so the whole
                           catalogue unit-tests with no host and no database.

CardiTrack.Infrastructure  EF configs + migrations, repositories, FcmNotificationChannel,
                           NotificationSnapshotQueries (set-based SQL) — each implementing
                           an Application port above.

CardiTrack.Worker          DataCompletenessWorker, NotificationDispatchWorker

CardiTrack.API             /api/v1/notifications/*  (inbox, actions, devices, prefs,
                           delivery acks, internal enqueue)

CardiTrack.Mobile.Core     Notification API client, inbox/settings view models
                           — the testable half, exercised by CardiTrack.UnitTests
CardiTrack.Mobile          Push registration + background handlers, OS channel
                           registration, permission prompt, inbox screen, dashboard card
```

Nothing here needs a sixth project, and no Application or Domain type gains a package
reference — the two invariants that keep the core testable without a host.

---

## 3. Three categories, one pipe

Category drives everything downstream: priority, whether quiet hours apply, which OS notification
channel it lands in, whether it escalates, and whether the user may silence it.

| | **Safety** | **Health** | **Nudge** |
|---|---|---|---|
| Content | Monitoring is down; nobody is listening | Red/orange anomaly in the wearer's data | A data gap the user can close |
| Source | `DataCompletenessWorker` | Alert generation (R1) / AI pipeline (R2) | `DataCompletenessWorker` |
| Push | Yes, immediate | Yes — red/orange; yellow is in-app + digest card | **In-app only**, except the two safety rules |
| Quiet hours | **Overridden** | Red overrides; orange defers | Always respected |
| Escalates (§6.3) | Yes | Red only | No |
| Silenceable | No — snooze ≤72h with logged acknowledgement | Sensitivity tuning (R2) | Yes — snooze, mute forever |
| OS channel | `carditrack.safety` | `carditrack.health` | `carditrack.nudges` (low importance) |

Health alerts keep their own `Alert` table and lifecycle (New → Acknowledged → Resolved). Nudges get
the `Notification` table. **Both produce `NotificationDelivery` rows** — that shared outbox is what
makes the reliability work in §6 apply uniformly, without merging two domain models that disagree
about almost everything else.

Nudges are deliberately near-silent. A caregiver who gets pushed about an empty medical-notes field
learns to swipe CardiTrack away, and takes the safety alerts with it. The two exceptions
(`DEVICE_AUTH_BROKEN`, `NO_ALERT_RECIPIENT`) are safety-category precisely because they mean
monitoring is not working.

---

## 4. Permission and reachability

Push is worthless without the OS grant, and the grant is a moment you get one shot at.

**Ask at the moment of value, not at launch.** The request fires after the first device connection
succeeds (`ConnectionSuccessPage`, M1-07), framed against what it buys: *"CardiTrack can alert you if
something looks wrong with Margaret — even when the app is closed."* Asking on `SplashPage` before
the user knows what the app does is the reliable way to get denied permanently.

**iOS** — request `alert + sound + badge`, plus `provisional` for the nudge channel so low-importance
items can be delivered quietly to Notification Center without a prompt. Two entitlements matter:

- **Time Sensitive** (`com.apple.developer.usernotifications.time-sensitive`, self-granted) —
  breaks through Focus modes. Applied to Safety and red Health.
- **Critical Alerts** (`com.apple.developer.usernotifications.critical`) — bypasses silent mode and Do
  Not Disturb entirely. Requires written approval from Apple, and an elderly-monitoring app with
  red alerts is squarely the use case they grant it for. **Worth applying for early** — approval is
  slow, and it is the difference between a red alert at 3am waking someone and not. Design assumes
  Time Sensitive; Critical Alerts is an upgrade the code should be ready to flip on per-category.

**Android** — `POST_NOTIFICATIONS` runtime permission (13+). Three notification channels registered at
first launch so the OS settings screen gives per-category control that mirrors ours; `carditrack.safety`
at `IMPORTANCE_HIGH`, `carditrack.nudges` at `IMPORTANCE_LOW`. High-priority FCM messages wake the app
from Doze.

**Detecting denial.** On every foreground the app reads the OS notification settings (authorization
status on iOS; `areNotificationsEnabled` plus per-channel importance on Android) and reports them with
the token registration. Denied, revoked, or the safety channel muted at OS level → the server arms
`PUSH_UNREACHABLE`, which surfaces in-app with a deep link to system settings. **A caregiver who has
silently turned off notifications is indistinguishable from one who is being reached, unless we
check.** This is the single most important reachability signal in the design.

---

## 5. Payload and platform mechanics

```jsonc
// FCM HTTP v1 — safety / red health alert
{
  "message": {
    "token": "<device token>",
    "data": {                          // no PHI — the app fetches content after auth
      "deliveryId": "...", "category": "safety",
      "deepLink": "carditrack://alerts/9b2f5f64",
      "ackToken": "...",               // HMAC, single-use, halts escalation  (§7.2 C3)
      "fetchToken": "..."              // HMAC, scoped to this delivery       (§7.2 C5)
    },
    "notification": { "title": "CardiTrack", "body": "Urgent — tap to view" },
    "android": {
      "priority": "high",              // wakes from Doze
      "ttl": "1800s",
      "collapse_key": "alert-9b2f5f64",
      "notification": { "channel_id": "carditrack.safety" }
    },
    "apns": {
      "headers": {
        "apns-priority": "10",
        "apns-push-type": "alert",
        "apns-collapse-id": "alert-9b2f5f64",
        "apns-expiration": "<now+1800>"
      },
      "payload": { "aps": {
        "interruption-level": "time-sensitive",   // or critical, once entitled
        "sound": "default", "badge": 3, "mutable-content": 1
      }}
    }
  }
}
```

- **`collapse_key` / `apns-collapse-id`** — a device offline for six hours must not receive six copies
  of the same stale device-silence alert. Keyed on the alert identity, the OS keeps only the newest.
- **TTL / `apns-expiration`** — a red alert that could not be delivered for 30 minutes is stale; let it
  expire rather than surprise someone at midnight with a morning event. The inbox still has it.
- **`mutable-content`** — the iOS notification service extension fetches the real title/body over
  authenticated HTTPS and rewrites the notification before display, so rich content reaches the lock
  screen without ever transiting APNs. This is what makes §7.1's privacy default cost nothing. The
  extension authenticates with the payload's `fetchToken`, **never the user's access token** — see
  §7.2 C5, where sharing the token via a keychain access group is rejected.
- **Silent push** (`content-available`, `apns-push-type: background`) is used only for the daily token
  liveness probe (§6.3). iOS throttles these hard and no alert may depend on one.

---

## 6. Reliability — the actual objective

Push is best-effort by design: APNs and FCM accept a message and give no read receipt. "Reliable"
therefore has to be *engineered above* the transport, and *measured*.

### 6.1 Service level

| Category | Target |
|---|---|
| Safety / red Health | **Dispatched to the provider within 30s** of enqueue, and **99% acknowledged by at least one recipient device within 60s** |
| Escalation trigger | No ack within **120s** |
| Orange Health | 99% within 5 minutes (quiet hours deferred) |
| Nudge | Next app open — no delivery target |

The two numbers measure different things and both are needed. **30s dispatch** is
`solution_manifest.md`'s stated *"alert delivery latency"* KPI and covers what we control; **60s ack**
covers whether it actually landed, which is what the caregiver experiences. Reporting only the first
is how a system claims 100% delivery into a void.

Undelivered-critical is a **paged operational event**, not a metric anyone reads next quarter.

### 6.2 Attempt, retry, dead-letter

`INotificationChannel.SendAsync(delivery, ct) → Sent | Retryable | Permanent(reason)`.

Immediate in-process attempt; on `Retryable`, `NotificationDispatchWorker` retries with backoff
(15s, 60s, 5m, 30m, 2h) up to the message TTL, then `DeadLettered` with a Warning log — matching the
orphan-cleanup precedent. `Permanent` (FCM `UNREGISTERED`, APNs `410`) disables the token immediately,
which arms `PUSH_UNREACHABLE`: **the failure feeds the engine back.**

Outbox rows are claimed `FOR UPDATE SKIP LOCKED` so a scaled-out Worker never double-sends.

**Dedup keys are namespaced by producer** — `worker:device-silence:{connectionId}:{utcDate}`,
`pipeline:device-silence:…`. Cross-producer suppression (the Worker's 48h `DEVICE_STALE_LONG` versus
the pipeline's 2h device-check) is then an **explicit collapse rule evaluated at send time**, not an
accidental unique-constraint violation. A single shared namespace would let either producer — or
anyone who reached the enqueue endpoint (§7.2 C4) — pre-claim a key and silently drop the other's
legitimate alert. Suppressing a safety alert must never be something a `UNIQUE` index does by
side effect.

### 6.3 Escalation runs across people, not vendors

With no email or SMS, the fallback for an unreachable caregiver is **another caregiver**. For Safety
and red Health:

```
t+0     push every registered device of the primary recipient
t+120s  no ack  → re-push at highest priority; mark recipient unreachable
t+300s  no ack  → fan out to all other caregivers with ReceiveAlerts on,
                  copy flagged "Escalated — nobody has acknowledged this yet"
t+900s  no ack from anyone → mark UNDELIVERED_CRITICAL; page ops; the alert
                  is pinned to every dashboard as an unmissable banner
```

**In R1 the fan-out stage has no targets on a family account.** `MaxUsers = 1`
([user_onboarding_process.md](./user_onboarding_process.md) Step 3), so the ladder degrades to
push → re-push → `UNDELIVERED_CRITICAL` → page ops, with no human fallback. That is worth building
anyway — the t+900s state is what turns a silent failure into a known one — but the design should not
be read as delivering redundant human coverage before **R3 family invitations**. Business accounts
(`MaxUsers = 20`) get the full ladder immediately and are out of MVP scope.

**The fan-out copy does not name who failed to respond.** *"Margaret's primary contact hasn't
responded"* would disclose one caregiver's behaviour — whether they looked at their phone — to
another, which is a new processing purpose for that person's data under GDPR and needs a lawful basis
recorded before it ships. The de-identified wording carries identical urgency at zero cost, so it is
the default. A named variant stays available only behind the family-sharing terms plus a DPIA entry
(§17.4).

This is strictly better than an email fallback would be: a second human with a working phone beats a
message in an inbox nobody watches. It also means a single dead phone cannot silence a family.

**Ack, don't assume.** The app POSTs `/notifications/{id}/delivered` the moment the push arrives —
from the background handler, before any user interaction — and again on open (`/seen`). Delivered and
seen are tracked separately: delivery proves the pipe works, seen proves the human engaged.

**Token liveness.** A daily silent push per device, plus the foreground registration heartbeat. A token
that has not acked anything in 7 days is presumed dead, disabled, and arms `PUSH_UNREACHABLE`.
Catching a dead token on a quiet Tuesday is the whole point — the alternative is discovering it during
the emergency.

### 6.4 Foreground reconciliation

Every app foreground triggers a full inbox sync. Push is an optimisation for *latency*; the sync is
what guarantees *eventual* correctness. If APNs dropped a message, the OS throttled a wake, or the
phone was off for a day, opening the app still shows the complete, current state — because the inbox
is a projection of server state, not a log of received events (§10.2).

---

## 7. Privacy and security controls

### 7.1 What lands on a lock screen

*"Margaret hasn't moved today"* on a lock screen in a shared home is a disclosure, and it is PHI in
transit through Apple's and Google's infrastructure.

**Default: content-free payloads.** The push carries identifiers and a deep link; the title is
CardiTrack, the body a category-level teaser. The iOS notification service extension (and Android's
data-message handler) fetches the real copy over authenticated HTTPS and rewrites the notification
before it displays. The user sees rich content; APNs and FCM never do.

**Opt-in richness.** A setting — *"Show alert details on the lock screen"* — lets a caregiver who
values speed over discretion get the full text directly in the payload. Off by default; the choice is
theirs to make knowingly. The send-time branch on this flag **must fail closed**: a null, unreadable
or unmigrated preference resolves to content-free, because the failure mode of the opposite default is
PHI in a third-party transport.

Because the default keeps health data out of the transport, Apple and Google act as conduits rather
than processors, and no BAA is required for the push path. **Confirm this with counsel before launch**
— it is the standard reading, not a settled fact, and the opt-in path changes the analysis. Raw metric
values stay out of push bodies regardless, per llm_design's family-audience rule.

**Accepted risk — metadata still discloses.** A CardiTrack critical-alert sound at 3am tells everyone
in earshot that a health emergency is occurring, with zero body text. The notification's existence,
timing and sound are the signal; content-free payloads do not remove it. This is inherent to the
feature and is accepted, not mitigated — recorded here so §7.1's "no health data" claim is read as
being about payload contents, not about the disclosure surface as a whole.

### 7.2 Threat model and controls

Reviewed against STRIDE. Five controls are load-bearing; each is a design commitment, not a
suggestion, and all five are cheaper now than after rows exist.

| | Control | Threat |
|---|---|---|
| **C1** | Notification rows carry no direct identifiers | Information Disclosure |
| **C2** | Push tokens encrypted, bounded, erasable | Information Disclosure / Spoofing |
| **C3** | Delivery acks authorized by per-delivery HMAC | Denial of Service / Spoofing |
| **C4** | Enqueue endpoint pins caller identity, not just audience | Spoofing / Elevation of Privilege |
| **C5** | Notification extension uses a scoped fetch token | Information Disclosure |

**C1 — no names in the clinical plane.** `TemplateData` must never hold a wearer's name. A row
carrying `{"name":"Margaret"}` next to `CardiMemberId` and a health-derived gap is an
identifier↔clinical join in plaintext, readable by `app_rw` — exactly what the tier split in
[data_protection_architecture.md](./data_protection_architecture.md) §2–3 exists to prevent, and a
second instance of its recorded **[GAP] #1**.

```csharp
// VULNERABLE — name persisted in the clinical plane
TemplateData = JsonSerializer.Serialize(new { name = member.Name, n = 4 });

// SECURED — pseudonym + non-identifying counters; name resolved per request at render time
TemplateData = JsonSerializer.Serialize(new { n = 4 });        // CardiMemberId is already on the row
var member = await _cardiMembers.GetByIdAsync(notification.CardiMemberId);   // today
return notification.Render(locale, member.Name);
```

**This control does not wait on the identity vault.** `pii.subject_identities` is net-new in
[data_protection_architecture.md](./data_protection_architecture.md) §3.2 and not built; names live in
`CardiMembers` today, reachable through the existing `ICardiMemberRepository`. The load-bearing rule
is *never persist the name into `TemplateData`* — the read path is an implementation detail that swaps
to `IIdentityVaultService` in one place when the vault lands. C1 is shippable in Phase 1 as written.

Resolution happens in `NotificationService` (Application) while projecting the response DTO, not in
the controller — controllers stay thin per the solution's convention, and a Domain entity must not
reach the API surface.

> **The trap:** encrypting `TemplateData` is the wrong fix. It leaves the name in the clinical plane's
> key management, makes the column unqueryable, and still hands `app_rw` the ciphertext. Don't store it.
> Render-time resolution is also the correct call for localization — a name is not a translatable
> resource, which is why §8 already commits to keys rather than strings.

**C2 — push tokens are Tier 1 data.** They are already classified Tier 1 on arrival by
[data_protection_architecture.md](./data_protection_architecture.md) §2 and enumerated under HIPAA
Safe Harbor category 13 (device identifiers) in its §4.2. Storing them in the clear would contradict a
policy that predates this design. A token is a stable cross-reinstall device identifier, and one
leaked alongside the FCM credential lets an attacker push to a named caregiver's phone — bypassing Do
Not Disturb once the Critical Alerts entitlement lands (#106).

```csharp
// VULNERABLE
entity.Token = request.PushToken;

// SECURED — the pattern DeviceConnection OAuth tokens already use (IEncryptionService, AES-256-GCM)
entity.Token            = _encryption.Encrypt(request.PushToken);
entity.TokenFingerprint = Convert.ToHexString(
    SHA256.HashData(Encoding.UTF8.GetBytes(request.PushToken)));   // upsert/lookup key
```

Disabled tokens are **hard-deleted at 30 days**, not soft-retained for 180; `PushDeviceToken` is
enumerated in the Safe Harbor export exclusion (the transform fails closed only for tables it knows
about) and in the erasure sweep.

**C3 — the ack endpoint decides whether a red alert escalates.** `POST /notifications/{id}/delivered`
halts the escalation ladder, which makes it the highest-value forgery target in the system: a
successful attack silently cancels the alert that says *check on Margaret*, and looks identical to
success. Two failure modes bracket it —

- Requiring a full user JWT is brittle: iOS background handlers routinely run with an expired access
  token (1-hour lifetime, validated at **zero clock skew** — `Auth0Extensions.cs:62`). Acks fail,
  escalation fires spuriously, and the family is woken at 3am for an alert that did arrive.
- Relaxing it to compensate — the trap — makes a guessable GUID sufficient to suppress an emergency.

The resolution is the pattern already used by the OAuth bounce (`DevicesController.cs:236`):
`[AllowAnonymous]`, authorized by a single-use token rather than a session.

```csharp
// VULNERABLE — brittle (expired JWT) or forgeable (GUID alone)
[HttpPost("{id}/delivered")]
public async Task<IActionResult> Delivered(Guid id) => Ok(await _svc.MarkDeliveredAsync(id));

// SECURED — per-delivery HMAC, single-use, bound to the device the push was sent to
[AllowAnonymous]
[HttpPost("{id}/delivered")]
public async Task<IActionResult> Delivered(Guid id, [FromBody] AckRequest req)
{
    // ackToken = HMAC-SHA256(deliveryId | pushDeviceTokenId | expiresAt), key in Secret Manager
    if (!_ackTokens.Validate(req.AckToken, id, out var deviceTokenId))
        return NotFound();                    // non-disclosure, matching the alerts convention
    return Ok(await _svc.MarkDeliveredAsync(id, deviceTokenId));   // replay is a no-op
}
```

Binding to `pushDeviceTokenId` stops a token lifted from device A acking device B's delivery. Add an
endpoint-specific rule to `IpRateLimiting.GeneralRules` (`appsettings.json:47`) — the global 100/min
is per-IP and too loose for a forgery target.

**C4 — audience-pinning alone does not secure the enqueue endpoint.** Any GCP principal can mint an
ID token with an arbitrary `aud`, so validating the audience without the caller's identity leaves an
unauthenticated ingest path into the alert pipeline: fabricate a red alert, or pre-claim a dedup key
to suppress a real one (§6.2).

```csharp
// VULNERABLE — audience alone
options.TokenValidationParameters = new() { ValidAudience = cfg["Pipeline:Audience"] };

// SECURED — pin the issuer, the audience, and the calling service account
options.TokenValidationParameters = new()
{
    ValidIssuer      = "https://accounts.google.com",
    ValidAudience    = cfg["Pipeline:Audience"],
    ValidateLifetime = true
};
options.Events = new JwtBearerEvents
{
    OnTokenValidated = ctx =>
    {
        var email    = ctx.Principal?.FindFirst("email")?.Value;
        var verified = ctx.Principal?.FindFirst("email_verified")?.Value == "true";
        if (!verified || !string.Equals(email, cfg["Pipeline:ServiceAccount"], StringComparison.OrdinalIgnoreCase))
            ctx.Fail("caller is not the pipeline service account");
        return Task.CompletedTask;
    }
};
```

Defence in depth: put the route behind Cloud Run IAM (`roles/run.invoker` granted only to the pipeline
service account) so the platform rejects unauthorized callers before app code runs.

**C5 — the notification service extension gets a scoped token, not the user's.** The NSE runs in a
separate process on every push. Sharing the access token to it through a keychain access group widens
credential exposure to the most frequently-executed code in the app. It receives `fetchToken` in the
payload instead — same HMAC construction as C3, scoped to one delivery, useless for any other call.

**Critical Alerts abuse surface.** The entitlement bypasses Do Not Disturb and silent mode, so a
compromised enqueue path or a bad severity gate could wake every user at 3am — reputational damage,
App Store review risk, and Apple can revoke the entitlement. Two controls: the `critical` flag is set
**server-side only**, from an allowlist of `(category, severity)` pairs, never derived from client or
producer input; and a circuit breaker halts sending and pages if critical enqueues exceed a threshold
in a window. The breaker is what keeps a C4 failure noisy rather than catastrophic.

---

## 8. Data model

```
Notification                            -- a nudge: one open gap, per target user
├── Id, OrganizationId, UserId, CardiMemberId?
├── RuleCode string(64), RuleVersion int
├── Category enum (Safety|Blocking|Unlock|Account), Priority enum
├── Fingerprint string(128) UNIQUE      -- SHA256(RuleCode|UserId|ScopeId|Discriminator)
├── TitleKey / BodyKey / BenefitKey     -- localization keys, not baked strings
├── TemplateData jsonb                  -- {"n":4} — counters ONLY. No names, no metric
│                                          values, no free text. Names resolve at render
│                                          time in NotificationService (§7.2 C1)
├── ActionDeepLink string(256)
├── State enum (Open|Snoozed|Resolved|Superseded)
├── SnoozedUntil, ResolvedDate, ResolutionReason
├── FirstDetectedDate, LastEvaluatedDate, FirstSeenDate, IsOwner
└── IsActive, CreatedDate, UpdatedDate

NotificationDelivery                    -- transactional outbox; BOTH producers write here
├── Id, SourceType (Alert|Notification), SourceId, UserId
├── CardiMemberId?                      -- denormalised from the source row at enqueue.
│                                          SourceId is polymorphic and cannot be joined
│                                          generically, so without this the ErasureWorker
│                                          sweep has nothing to filter on
├── Category enum, Channel (Push|InApp)
├── State (Pending|Sent|Delivered|Suppressed|Failed|DeadLettered|Undelivered)
├── PushDeviceTokenId?, DedupKey UNIQUE, CollapseKey, ExpiresAt
├── ScheduledFor                        -- quiet-hours deferral lands here
├── Attempts, NextAttemptAt, LastError, ProviderMessageId
├── SentDate, DeliveredDate             -- DeliveredDate = client ack (§6.3)
└── EscalationStage int, EscalatedFrom Guid?

PushDeviceToken                         -- Tier 1 data (§7.2 C2)
├── UserId, DeviceId, Platform (Ios|Android), AppVersion
├── Token            string             -- ENCRYPTED, AES-256-GCM via IEncryptionService
├── TokenFingerprint string(64)         -- SHA-256 hex; upsert/lookup key, since the
│                                          ciphertext is non-deterministic
├── OsAuthorizationStatus enum, SafetyChannelEnabled bool   -- §4 reachability
├── LastSeenDate, LastAckDate, DisabledDate, DisabledReason
└── UNIQUE(UserId, DeviceId), UNIQUE(TokenFingerprint)

NotificationPreference                  -- per user
├── UserId UNIQUE
├── QuietHoursStart/End TimeOnly?       -- rendered against User.TimeZoneId
├── ShowDetailsOnLockScreen bool        -- §7, default false
└── MutedCategories jsonb

NotificationMute                        -- the "don't ask again" record
├── UserId, RuleCode?, Category?, CardiMemberId?
├── MutedDate, MutedUntil?              -- null = forever
└── AcknowledgedConsequence bool        -- true only for Safety-class dismissals

NotificationRunLog                      -- one row per DataCompletenessWorker run (§13)
├── StartedAt, CompletedAt, LastOrganizationId      -- resume point after a crash
├── OrgsScanned, Created, Resolved, Suppressed
└── DurationMs, Error?                  -- a misfiring rule is one query away
```

`NotificationDelivery` is polymorphic over `SourceType` rather than FK'd to one table — it is the
shared reliability substrate, and an `Alert` and a `Notification` have nothing else in common.

**Why `Fingerprint`:** nudge reconciliation is idempotent because identity is content-derived. Running
the worker twice produces zero duplicates without a distributed lock. The discriminator segment makes
a *changed* gap a new notification — sleep sparsity carries the fortnight bucket, so "still sparse next
fortnight" re-arms after a mute expiry while "still sparse tomorrow" does not.

---

## 9. Nudge rule catalogue

Each rule declares detection, the capability it unlocks (the copy the user reads), where tapping lands,
priority, and silence policy. `Full` = snooze + mute-forever · `Snooze` = time-boxed, re-arms ·
`Safety` = cannot be muted; ≤72h snooze, dismissal records an acknowledgement (§11).

### Safety — monitoring is degraded or nobody is listening *(pushes)*

| Code | Detection | Copy | Silence | Wave |
|---|---|---|---|---|
| `DEVICE_AUTH_BROKEN` | `ConnectionStatus` ∈ {`TokenExpired`, `AuthError`} | "Reconnect to restore monitoring — no data is reaching CardiTrack right now." | Safety | **R1** |
| `DEVICE_BATTERY_LOW` | `BatteryLevel ≤ 10` or `BatteryStatus` ∈ {`Low`, `Empty`}, reading < 24h old, no broken grant outstanding | "{Name}'s watch is almost out of battery — charging it now keeps monitoring unbroken." | Safety | **R1** |
| `PUSH_UNREACHABLE` | OS permission denied/revoked, safety channel muted, token dead 7d, or `Permanent` send failure | "Alerts can't reach this phone. Turn notifications on so urgent alerts get through." | Safety | R2 (with push) |
| `NO_ALERT_RECIPIENT` | Every active `UserCardiMember` has `ReceiveAlerts = false` | "Nobody is set to receive {Name}'s alerts. Turn one on so a red alert reaches someone." | Safety | **R3** |

> `NO_ALERT_RECIPIENT` is R3, not R1. Family accounts have **`MaxUsers = 1`**
> ([user_onboarding_process.md](./user_onboarding_process.md) Step 3), so in R1 there is exactly one
> caregiver per family account and `ReceiveAlerts` defaults true — the rule can only fire if that sole
> user disables their own alerts. It becomes real with family invitations in R3. Business accounts
> (`MaxUsers = 20`) are the exception, and are out of MVP scope.

### Blocking — core value is unavailable *(in-app)*

| Code | Detection | Copy | Priority | Silence | Wave |
|---|---|---|---|---|---|
| `DEVICE_REMOVED` | No active `DeviceConnection` for a member that previously had one | "{Name} has no connected wearable. Reconnect one to resume monitoring." | Critical | Snooze 7d | **R1** |
| `DEVICE_STALE_LONG` | `LastSyncDate` > 48h | "{Name}'s watch hasn't synced in two days. A charge or a phone-app open usually fixes it." | High | Snooze 3d | **R1** |
| `TIMEZONE_DEFAULT` | `TimeZoneId = "UTC"` and `Locale` implies otherwise | "Set your time zone so 'no activity yet today' and daily summaries use *your* clock." | High | Snooze 30d | **R1** |
| `BASELINE_STALLED` | `daysCaptured` flat 7d, < 80% coverage gate | "{Name} is {n}/30 days into learning. Alerts switch on once the picture is complete." | High | Snooze 14d | **R1** |

> Renamed from `NO_DEVICE_CONNECTED`, which could not fire as originally written: §10.4 suppresses
> nudges while `IsOnboardingComplete = false`, and connecting a device *is* onboarding Step 6. The
> reachable case is a device removed or disconnected later, which is what this now detects.

### Capability unlocks — "submit this, get that" *(in-app)*

| Code | Detection | Copy | Priority | Silence | Wave |
|---|---|---|---|---|---|
| `SLEEP_SCOPE_MISSING` | `Scopes` lacks the sleep bundle | "Grant sleep access so CardiTrack can track {Name}'s sleep patterns and nightly trends." | High | Full | **R1** |
| `MEDICAL_NOTES_EMPTY` | `MedicalNotes` null/empty | "Conditions and medications make AI insights and the doctor-visit report far more specific. Encrypted at rest, visible only to your family." | Low | Full | **R1** |
| `EMERGENCY_CONTACT_MISSING` | `EmergencyContactName`/`Phone` null | "Add an emergency contact so the right person is on file when something looks wrong." | High | Full | R2 |
| `MEMBER_CONTACT_MISSING` | `CardiMember.Phone` null | "Add {Name}'s number to call or text straight from an alert." | Low | Full | R3 |
| `NO_PRIMARY_CAREGIVER` | No `IsPrimaryCaregiver` among active links | "Name a primary caregiver so urgent alerts have a clear first responder." | Medium | Full | R3 |

**Copy must not promise what isn't built.** Two rules originally did:

- `SLEEP_SCOPE_MISSING` promised *"unlock sleep-disruption alerts"* — at the time, `AlertType.Sleep`
  had no generator, so it was reworded to the tracking and trends that shipped then. `IrregularSleepRule`
  has since landed in `StatisticalAlertWorker`, so the copy may now promise sleep alerts.
- `EMERGENCY_CONTACT_MISSING` promised *"a one-tap call button"*, which lives on **M1-12 Alert
  Detail – Critical — ⬜ not built, no Figma frame**. Reworded, and held to R2 so the full promise
  ships with the screen that honours it.

A nudge asks a caregiver for effort in exchange for a capability. Naming one we haven't shipped spends
trust with exactly the user whose cooperation the engine depends on.

**Cut:**

- `DOB_MISSING` — promised *"age-appropriate heart-rate thresholds"*. No such mechanism exists or is
  designed; the alert rules are z-scores over the member's own baseline. The benefit was fictional, so
  the rule goes rather than getting reworded. (The nullable-`DateOfBirth` migration in §14 stays — it
  is a real schema defect either way.)
- `MONITORING_PAUSE_ENDED` — purely informational, and M1-13 (shipped) already displays pause state.
  Inbox noise.

**Demoted out of the catalogue:**

- `SLEEP_DATA_SPARSE` — the fix is *"get an elderly person to wear a watch overnight."* The caregiver
  often cannot control that and the wearer has no login in R1 (§17.3), so no one who sees the nudge
  can reliably act on it. Low agency means a low comply rate, which means the §13 review gate would
  retire it within a quarter. Ships as a **progress indicator on the member dashboard** instead —
  same information, no demand attached.

### Account & lifecycle *(in-app)*

`PAUSE_LEFT_LONG` (paused >14d, Medium, snooze 7d, **R1**) · `TRIAL_EXPIRING` (7/3/1 days, High, R2
with billing) · `CONSENT_NOT_RECORDED` (Safety class, ships with per-metric consent).

**Authoring rules.** Benefit-first, guilt-free — name the capability, never "you failed to". No raw
metric values. One rule = one gap = one action; if the fix needs two screens, it is two rules. Every
rule deep-links to the exact field, not a settings root.

---

## 10. Nudge evaluation

### 10.1 Snapshot, then pure rules

```csharp
public interface INudgeRule
{
    string    RuleCode { get; }
    int       Version  { get; }
    NudgeSpec Spec     { get; }                     // category, priority, silence policy
    NudgeVerdict Evaluate(NudgeContext context);    // pure: no I/O, no clock, no DbContext
}
```

`NudgeContext` is a pre-fetched per-member snapshot — user, member, connections, parsed scopes, token
reachability, existing notifications, active mutes, baseline state, and aggregate coverage counts
(`nullSleepDays14`, `distinctDataDays30`) computed set-based in SQL, never by loading `ActivityLog`
rows. `utcNow` is a context field, so every rule is deterministic and table-testable. This mirrors
`BaselineProgress`, already a pure static over pre-fetched inputs.

### 10.2 Reconcile, don't insert

| Desired | Stored | Action |
|---|---|---|
| present | absent | Insert `Open`; enqueue delivery if the category pushes |
| present | `Open` | Touch `LastEvaluatedDate`, refresh `TemplateData` |
| present | `Snoozed` expired / live | → `Open` / leave alone |
| present | muted | Skip entirely — no row |
| absent | `Open`/`Snoozed` | → `Resolved`, `ResolutionReason = GapClosed` |

**The user never dismisses a nudge they fixed.** Auto-resolution keeps the inbox honest, and makes it a
projection of current state rather than a log — which is what lets §6.4's foreground sync be a complete
answer rather than a patch.

Member soft-deleted or org deleted → `Resolved`; monitoring paused → `Superseded`.

### 10.3 Who gets nudged

1. **Ownable gaps** (member profile, device, scopes) → primary caregiver; fall back to earliest-assigned
   active caregiver with `CanViewHealthData`, then org `Admin`.
2. **Personal gaps** (`TIMEZONE_DEFAULT`, `PUSH_UNREACHABLE`) → that user only.
3. **Org gaps** (`TRIAL_EXPIRING`) → `Admin`, or the single family `Member`.
4. Everyone else gets `IsOwner = false`: visible read-only, never pushed, never on their dashboard.
5. Ownership re-targets if the owner deactivates; the fingerprint is stable across re-targeting so a
   handover doesn't reset a snooze.

Health alert routing is separate and deliberately wider — every caregiver with `ReceiveAlerts` gets a
red alert. Nudges have one owner; emergencies have all hands.

### 10.4 Suppression

No nudge created when the account is <48h old (except `DEVICE_REMOVED`, which is precisely the case
where a new account has lost its only connection),
the member is paused, an unacknowledged red `Alert` is open for that member, or onboarding is
incomplete. Safety-category rules ignore all of these except the pause.

---

## 11. Comply, silence, and not being a nuisance

| Affordance | Effect | Re-arm |
|---|---|---|
| **Comply** | Deep link to the exact field; on save the gap closes and the row resolves synchronously (§12) | n/a |
| **Snooze** | `SnoozedUntil = now + rule default` (3–30d); user picks 1d / 1w / 1m | Automatic |
| **Dismiss** | Writes `NotificationMute`, resolves the row | Only if `RuleVersion` increases |

**Safety-class rules cannot be muted.** `DEVICE_AUTH_BROKEN`, `DEVICE_BATTERY_LOW`,
`NO_ALERT_RECIPIENT`, `PUSH_UNREACHABLE`
and `CONSENT_NOT_RECORDED` are where silence means an unmonitored person or an unlawful basis for
processing. Max 72h snooze; the dismiss action instead opens a consequence confirmation ("Alerts for
Margaret will stay off until you turn them back on"), recording `AcknowledgedConsequence` and an
`AuditLog` entry. A deliberate, logged, reversible user choice — not an override we refuse.

**Quiet hours** apply to Nudge and orange Health, never to Safety or red Health — that asymmetry is
the entire reason the Critical Alerts entitlement is worth pursuing. Evaluated against
`User.TimeZoneId`, which is why `TIMEZONE_DEFAULT` is High priority.

**Budget.** Push: Safety and red Health are uncapped (capping an emergency is indefensible); orange
Health coalesces to at most one per member per 6h; Nudges never push. In-app: 3 new nudge rows per user
per run, 2 dashboard cards, inbox uncapped and priority-ranked.

**Escape hatches**, one settings screen: mute a category, mute a rule, quiet hours, lock-screen detail
toggle, and **"Show me everything again"** to clear all mutes. Every mute is listed and reversible
there — a silence the user can't find later is a bug.

---

## 12. API surface

Extends [notifications.md](../execution/backend/api/notifications.md); `ApiResponse<T>` envelope,
integer enums, `ICardiMemberAccessService` scoping (unreadable member → **404**).

| Endpoint | Purpose |
|---|---|
| `POST` / `DELETE /api/v1/notifications/devices` | Token upsert / unregister. Upsert also carries OS authorization status and per-channel enablement (§4) and doubles as the reachability heartbeat |
| `POST /api/v1/notifications/{id}/delivered` | **Client ack** — posted from the background push handler. Drives the SLO and stops escalation. `[AllowAnonymous]`, authorized by the payload's single-use `ackToken` rather than a user JWT, and separately rate-limited (§7.2 C3) |
| `GET /api/v1/notifications` | Inbox. Filters `state`, `category`, `cardiMemberId`, `owned`, `limit` (≤200), `offset` |
| `GET /api/v1/notifications/summary` | Unseen count, safety banners, top 2 cards. One call on launch and foreground sync |
| `POST /api/v1/notifications/{id}/seen` · `/snooze` · `/dismiss` | Funnel + the three affordances. Dismiss requires `acknowledgedConsequence` for Safety, else **400** |
| `GET` / `PUT /api/v1/notifications/preferences` | Quiet hours, muted categories, lock-screen detail |
| `GET /api/v1/notifications/mutes` · `DELETE /mutes/{id}` · `POST /mutes/reset` | The silence surface |
| `POST /api/v1/internal/notifications/enqueue` | **Service-to-service only.** Google OIDC ID token validated on issuer, audience **and the calling service account's verified `email`** — audience-pinning alone admits any GCP principal (§7.2 C4). Behind Cloud Run IAM `roles/run.invoker`; unreachable with a user JWT |

All actions idempotent; 404 (never 403) on another user's row, matching the alerts convention.

**Synchronous resolution:** saving an emergency contact clears the card before the screen pops. The
worker is the backstop, not the only path.

This is a **direct service call, not a domain event.** There is no event infrastructure in the
solution — no MediatR, no dispatcher — and adding one for this feature would put a single corner of
the codebase on a pattern nothing else uses, against the standing "Application services, not a
mediator" convention. The established shape is service-to-service composition through an Application
port, exactly as `OnboardingService` already injects `ISubscriptionService`:

```csharp
// Application/Interfaces/Services/INotificationGapResolver.cs
public interface INotificationGapResolver
{
    Task ResolveForCardiMemberAsync(Guid cardiMemberId, CancellationToken ct);
    Task ResolveForUserAsync(Guid userId, CancellationToken ct);
}

// CardiMemberService / DeviceConnectionService / UserService — after the write
await _unitOfWork.SaveChangesAsync();
await _gapResolver.ResolveForCardiMemberAsync(member.Id, ct);
```

**Cost, stated:** three services gain a constructor dependency, and resolution becomes an explicit
call rather than an implicit subscription. That is the right trade at this size — the implicitness is
precisely what makes an event bus expensive to debug, and the reconciler (§10.2) already guarantees
correctness if a call site is ever missed.

---

## 13. Workers, observability, and proving it works

Eight other workers exist in `CardiTrack.Worker`; this engine adds three — eleven in total, all
`CronBackgroundService` subclasses configured under `Workers:{Name}`.

| Worker | Cron | Job |
|---|---|---|
| `NotificationDispatchWorker` | `*/30 * * * * *` | Claim due outbox rows (`SKIP LOCKED`), retry, run escalation timers, expire past-TTL rows, disable dead tokens |
| `DataCompletenessWorker` | `0 0 6 * * *` | Evaluate + reconcile all active orgs, batched, cancellation honoured between batches |
| `PushCanaryWorker` | `0 */15 * * * *` | Send a real push to the canary fleet, alert if the ack doesn't come back (the synthetic canary below) |

30 seconds is the retry granularity the 60s SLO requires; detection runs at 06:00 UTC, after the 02:30
baseline recalculation.

**Retention** folds into `DataRetentionWorker` ([data_protection_architecture.md](./data_protection_architecture.md) §5.2),
or `PartitionMaintenanceWorker` if that lands first:

| Rows | Retention | Action |
|---|---|---|
| `Notification` — `Resolved`/`Superseded` | 180 days | Hard delete |
| `NotificationDelivery` — terminal states | 90 days | Hard delete |
| `PushDeviceToken` — disabled | **30 days** | **Hard delete** — Tier 1 identifiers, not soft-retained (§7.2 C2) |
| `NotificationMute` | Life of the user | Never purged — a silence the user chose is a preference, not exhaust |

Push tokens and notification rows join the erasure sweep in that document's §6.2, and
`PushDeviceToken` is enumerated in the Safe Harbor export exclusion — the transform fails closed only
for tables it knows about.

**Concurrency & failure.** Reconciliation is idempotent by fingerprint, so a crashed run simply
repeats. A `NotificationRunLog` row per run (§8) makes a misfiring rule visible in one query and lets
a run resume from the last completed org.

`CronBackgroundService` has **no distributed lock** — it gained a per-tick error boundary after the
2026-08-12 incident, but nothing serializes instances
([data_protection_architecture.md](./data_protection_architecture.md) §1 finding #12) — and
`infrastructure/main.tf` sets `cloud_run_max_instances = 3`. The dispatch worker is safe regardless:
`FOR UPDATE SKIP LOCKED` claiming means three instances divide the outbox rather than duplicate it —
the same property that makes horizontal scaling a throughput win instead of a correctness problem.
`DataCompletenessWorker` is *correct* under triple execution, since fingerprints deduplicate, but it
would run the full morning evaluation three times for one useful result. It takes a **Postgres
advisory lock** for the duration of the run — the same mitigation `DataRetentionWorker` needs, for
the same reason.

**Metrics** (Datadog APM is wired — `Apm:Engine`, PR #4):

- `notification.enqueued` / `.sent` / `.delivered` / `.failed{reason}` / `.dead_lettered` — by category
- `notification.time_to_ack` histogram — **the SLO metric**; alert when p99 for Safety breaches 60s
- `notification.escalated{stage}` and `notification.undelivered_critical` — **page on the latter**
- `notification.push_unreachable_users` gauge — how much of the base we cannot reach *right now*
- `notification.token_churn`, `.outbox_depth` — leading indicators
- Nudge funnel: `.seen` → `.complied` / `.snoozed` / `.muted`, plus `time_to_comply` per rule

**Synthetic canary.** `PushCanaryWorker` sends a real push to a fleet of test devices every 15 minutes and
alerts if the ack doesn't return. Provider outages, expired credentials and a broken APNs certificate
are all silent failures otherwise — the kind you discover from a support ticket after the emergency.
For the primary objective of this engine, a canary is not optional.

**Anti-nag gate, quarterly:** any nudge rule with comply rate <15% or mute rate >30% over 500+
impressions goes to **rework — copy, timing, or deep-link target — not automatic deletion.** The
engine is required scope, so a failing rule is evidence the prompt is wrong, not that the gap stopped
mattering. Deletion needs a product call, and Safety-class rules are never deleted on metrics alone.
The reason the gate exists at all is unchanged: a rule people silence trains them to ignore the app,
and takes the safety alerts down with it.

---

## 14. Existing debt this resolves

| Issue | Resolution |
|---|---|
| `NotificationPreferencesRequest` DTO is per-CardiMember with a registered validator no endpoint consumes | ✅ **Done** — DTO, validator and registration deleted. It described SMS/email channels this engine does not have; the R2 alert-preferences work should introduce the shape it actually needs rather than inherit this one |
| `UserCardiMember.NotificationPreferences` JSON blob (`{sms,email,push}`), read by nothing | ✅ **Done** — the column was dropped in `AddPushDeliverySpine`, with no data migration: SMS/email keys were discarded by decision (those channels are out of scope) |
| `CardiMember.DateOfBirth` non-nullable `DateOnly` | **Not a defect — leave it.** `CreateCardiMemberValidator` and `UpdateCardiMemberValidator` both require a date and enforce an 18–120 age range, and both are registered, so the API cannot produce a member without one. With `DOB_MISSING` cut (§9) nothing depends on a nullable column, and making it optional would ripple through age display, the insights prompt and two mobile screens to permit a state the product deliberately forbids. If DOB should become optional, that is a product decision with its own change |
| `OnboardingStatusResponse.HasNotificationPreferences`, `TotalSteps = 7` | **Still open, for a new reason.** Push has shipped, and the `NotificationPreference` table with its GET/PUT endpoints exists — but `UserService.cs:179` still hard-codes `HasNotificationPreferences = false` (TODO), so onboarding `CurrentStep` sticks at 7 regardless. Wire the real lookup, or drop the step — §17.6 |
| `AlertSensitivity` stored, consumed by nothing | Still inert here — and the gap has widened: it is now editable end-to-end (edit screen → API → stored) while still consumed by no alert producer. Noted so `NO_ALERT_RECIPIENT` isn't confused with sensitivity tuning |
| No `device_disconnected` alert type (alerts.md notes the gap) | Covered as Safety nudges `DEVICE_AUTH_BROKEN` / `DEVICE_STALE_LONG` / `DEVICE_BATTERY_LOW` rather than a sixth `AlertType`. A flat battery is a fact about hardware, not about the wearer — the `Alert` history stays clinical, and the Safety category already delivers harder than a red `Alert` does (immediate push, critical flag, quiet-hours override, escalation) |

---

## 15. Testing

- **Per-rule table tests** — pure over `NudgeContext`, no DB. Boundaries explicit: 6 vs 7 sleep samples,
  47h vs 49h staleness.
- **Reconciler idempotency** — run twice over a fixed snapshot: zero new rows, only `LastEvaluatedDate` moves.
- **State machine** — snooze expiry re-arms; `RuleVersion` bump re-arms a muted rule; mute suppresses
  creation; gap closed → `Resolved` with no user action.
- **Outbox** (`IntegrationTests`, Testcontainers — already in the harness) — `SKIP LOCKED` under two
  concurrent dispatchers sends exactly once; `Retryable` backs off; `Permanent` disables the token and
  arms `PUSH_UNREACHABLE`; past-TTL rows expire unsent.
- **Escalation** — no ack at 120s re-pushes; at 300s fans out to secondary caregivers; at 900s marks
  `UNDELIVERED_CRITICAL`. An ack at any stage halts the ladder, and a late ack does not un-escalate.
- **Quiet hours** — Safety and red Health pierce them; nudges defer. `America/New_York` at 23:30 local,
  and both DST transition days, without double-send or skip.
- **Targeting** — five caregivers, one owned nudge and four read-only; red alert reaches all five.
- **Reachability** — OS permission revoked between foregrounds arms `PUSH_UNREACHABLE` on next
  registration; a token silent 7 days is disabled.
- **Payload privacy** — assert no PHI field ever reaches the FCM request body with the default setting;
  a golden-payload test that fails loudly if someone interpolates a member name into a push body.
  A null/unmigrated lock-screen preference must resolve to content-free (§7.1 fail-closed).
- **Security controls** (§7.2), each with a negative test:
  - **C1** — persisting a notification with a name in `TemplateData` fails; the rendered API response
    still carries the name, proving resolution happens at read time.
  - **C2** — the token column is unreadable without `IEncryptionService`; upsert matches on
    fingerprint; a disabled token is gone at 30 days, not soft-flagged.
  - **C3** — an ack with a forged, expired, replayed, or other-device `ackToken` returns 404 **and
    does not stop escalation**. This is the test that matters most in the suite: it is the one whose
    absence looks exactly like success.
  - **C4** — an OIDC token with the right audience but a different service-account `email` is
    rejected; a pre-claimed dedup key from one producer does not suppress the other's alert.
  - **C5** — the extension's `fetchToken` cannot be replayed against any other endpoint.
- **Critical-alert gating** — the `critical` flag cannot be set from client or producer input; the
  circuit breaker halts sending past the threshold rather than delivering.
- **Device-lab matrix** — real iOS and Android hardware, app killed / backgrounded / Doze / airplane
  mode, plus at least one aggressive-OEM device. Emulators do not reproduce the failures that matter.
- **API** — cross-tenant 404; Safety dismiss without acknowledgement 400; every action idempotent.

---

## 16. Delivery phases

**The nudge engine shipped before the push spine, because it was the only notification content that
existed when Phase 1 was scoped.** At that point no code in the solution created an `Alert` row —
`AlertService` only read and acknowledged, and *Statistical alerts (all 5 launch types)* was ⬜ in the
[release matrix](../release_matrix.md). Alert generation has since shipped (statistical rules cover
all five types, and the matrix marks it so). A delivery spine built first would have carried nothing
but its own canary; data gaps, by contrast, were detectable on every existing account from day one.

**Phase 1 — In-app nudge engine (R1, ~1.5pm).** `Notification` + `NotificationMute` +
`NotificationRunLog` + migrations · `INudgeRule` + reconciler + snapshot queries ·
`DataCompletenessWorker` (advisory-locked) · inbox, dashboard card, settings screen ·
`INotificationGapResolver` wired into the three writing services · **six rules**: `DEVICE_AUTH_BROKEN`,
`DEVICE_REMOVED`, `DEVICE_STALE_LONG`, `TIMEZONE_DEFAULT`, `BASELINE_STALLED`, `SLEEP_SCOPE_MISSING`,
plus `MEDICAL_NOTES_EMPTY` and `PAUSE_LEFT_LONG` if they come free.

No push, no vendor, no BAA, no device lab, no entitlement dependency — it ships against surfaces that
already exist, which is why it can run alongside the two external R1 clocks (Fitbit sunset, Google
verification #39) without competing for the same people.

**Phase 2 — Alert generation (R1).** ✅ **Shipped separately** while this branch was in flight —
`StatisticalAlertWorker` (PR #118) and `InactivityDetectionWorker` (PR #116). It was the dependency
that made a delivery spine worth building, and it is now met: the `Alert` table is no longer
permanently empty. This engine's staleness rule defers to the device-silence alert (§9), which is
faster and asks the caregiver for the same thing.

**Phase 3 — Push delivery spine (originally R2, brought forward).** ✅ **Shipped** — `PushDeviceToken` +
`NotificationDelivery` + FCM HTTP v1 with APNs passthrough · device registration + ack endpoints ·
MAUI push registration, permission flow at M1-07, background handlers, OS notification channels ·
`NotificationDispatchWorker` · content-free payloads · escalation ladder + `UNDELIVERED_CRITICAL`
paging · token liveness (passive sweep — see §17) · `PUSH_UNREACHABLE` · quiet hours + preferences ·
`PushCanaryWorker` · `/internal/enqueue` (GoogleOidc scheme) wired for the pipeline's future
`SeverityRouter`. **Deferred:** the iOS notification service extension's Xcode project itself (§17).

> **Start the Apple Critical Alerts application now regardless (#106).** It is weeks of queue latency
> at Apple and costs nothing to have in hand early; #107 (APNs key) and #108 (Firebase) stay open for
> the same reason. Provisioning lead time is the one part of Phase 3 that does not benefit from being
> deferred.

**Phase 4 — Breadth (R2→R3).** `EMERGENCY_CONTACT_MISSING` with M1-12 · `TRIAL_EXPIRING` with billing ·
`CONSENT_NOT_RECORDED` with per-metric consent · the three multi-caregiver rules with family
invitations (R3) · web inbox parity when the web dashboard lands · push inline actions (matrix R4).

**Explicitly out of scope, still:** email and SMS (permanently), the multi-caregiver rules pending
family invitations (R3), and the web inbox. Push delivery, the escalation ladder, and cross-recipient
fan-out (for the current `MaxUsers = 1` account shape) shipped in Phase 3 ahead of the original R2
placement.

---

## 17. Decisions

### Settled

1. **Alert generation is funded for R1.** This is what keeps the push spine at R2 rather than R3 —
   there will be something to deliver. Tracked as #111, and it is the highest-value item in the R1
   notification story.
2. **The nudge engine is required scope, not a discovery bet.** No comply-rate validation gates it.
   The §13 review gate therefore sends a failing rule to rework rather than deletion — a rule nobody
   acts on means the prompt is wrong, not that the gap stopped mattering.
3. **Wearer-facing nudges are out of scope** — accepted in principle, but no phase picks them up, and
   this engine does not address them. The consequence is recorded rather than solved:
   `SLEEP_DATA_SPARSE` had nobody who could reliably act on it, which is why it became a dashboard
   indicator instead of a nudge (§9).
4. **Apply for Critical Alerts now** (#106) rather than shipping on Time Sensitive alone. Weeks of
   queue latency at Apple, nothing to lose by holding it early, and the design flips it on per
   category when granted.
5. **Escalation fan-out copy** does not name who failed to respond (§6.3), so no caregiver behaviour
   is disclosed and no new lawful basis is needed. A named variant would require the family-sharing
   terms plus a DPIA entry — open only if product wants it.

### Open

6. **Does the inbox count as PHI access?** It lists member names against health-data gaps.
   Recommendation: annotate `GET /api/v1/notifications` with `AuditHealthDataAccessAttribute` and
   update the DPIA. *Owner: engineering + compliance. Blocks nothing; cheap to do in Phase 1.*
7. **Push-path BAA position** (§7.1) — legal confirmation that content-free payloads make Apple and
   Google conduits rather than processors, and what the opt-in lock-screen setting changes.
   *Owner: legal. Blocks Phase 3, not Phase 1.*
8. **Rule catalogue in code or DB?** Proposed: code, with `RuleVersion` for re-arming. DB-driven rules
   allow copy changes without a deploy but put untested predicates in production data. *Owner:
   engineering. Decide before the catalogue grows past ~10 rules.*
9. **Web parity** — the web app is template-stage; Phases 1–3 are mobile + API. Browser push (Web
   Push/VAPID) is a separate integration, R4 per the matrix.

### Phase 3 implementation deviations

Decided during the build, not in the original design — recorded here so a later edit doesn't "fix"
them back to something worse.

10. **iOS Notification Service Extension deferred, not shipped.** §5/§7.1's `mutable-content` rewrite
    needs a real Xcode App Extension target — a class of project this environment cannot compile,
    sign, or sanity-check (no Mac). Rather than scaffold one blind and risk a malformed `.csproj`
    breaking the whole iOS CI build, its real, verifiable dependency shipped instead: the
    `GET /internal/notifications/{deliveryId}/content` endpoint and `NotificationContentService` the
    extension would call. The extension project itself is a follow-up with Mac-based verification.
    Until then, iOS notifications render the content-free teaser rather than the rewritten rich copy.
11. **Daily silent-push liveness probe simplified to a passive sweep.** §6.3 describes an active
    `content-available`/`apns-push-type: background` probe sent once a day per device. Shipped
    instead: `NotificationDispatchWorker` disables any `PushDeviceToken` with no ack in 7 days — the
    same signal source (`PushDeviceToken.LastAckDate`) but reactive rather than actively solicited.
    The active probe needs a new `INotificationChannel` method not yet built; tracked as a follow-up,
    not dropped.
12. **`Enqueued` counter and a precise `TimeToAck` histogram not wired.** Blocked by the
    Application-layer zero-package boundary (`CardiTrack.Application` cannot reference
    `CardiTrack.Infrastructure`'s `PushTelemetry`) and by `IAckDeliveryService.MarkDeliveredAsync` not
    currently returning elapsed time. Lower priority than the rest of §13's metrics, which are wired.
13. **`NotificationDispatchWorker` claims via `FOR UPDATE SKIP LOCKED`, not `DataCompletenessWorker`'s
    advisory lock.** The advisory lock serializes an entire run across instances — correct for a
    once-daily batch job, wrong for a 30-second loop that needs several Cloud Run instances dividing
    the outbox in parallel (§13). Proven under real concurrent load in
    `OutboxConcurrencyTests` (`tests/CardiTrack.IntegrationTests/Notifications/`), not just asserted.
14. **The HMAC ack-token secret is injected into the API and Worker only, not the pipeline jobs** —
    unlike `Encryption__Key`, which reaches four hosts. The AI pipeline never sends push directly; it
    calls `/internal/enqueue` and the API does the actual send, so pipeline jobs have no use for the
    key. Least-privilege, not an oversight to "complete" later.

---

**Owner:** Engineering
**Last Updated:** August 13, 2026
