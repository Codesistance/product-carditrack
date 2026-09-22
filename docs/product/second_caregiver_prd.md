# PRD: Second Caregiver (Family Sharing, Phase 1)

**Surfaces:** API · Mobile · **Wave:** R1 · **Plan gate:** none (family-member ceiling enforced, never sold)
**Status:** Decisions taken 2026-09-22 · **Author:** m.softdir@gmail.com · **Last updated:** 2026-09-22

## Table of Contents

0. [Decisions taken](#0-decisions-taken)
1. [The Problem](#1-the-problem)
2. [Success Metrics](#2-success-metrics)
3. [Out of Scope](#3-out-of-scope)
4. [Competitive Position](#4-competitive-position)
5. [User Stories & Acceptance Criteria](#5-user-stories--acceptance-criteria)
6. [Open Questions](#6-open-questions)
7. [Risk & Dependency Check](#7-risk--dependency-check)
8. [Build Order](#8-build-order-one-builder-serialised)
9. [Surface & Release Placement](#9-surface--release-placement)
10. [RICE](#10-rice)
11. [References](#11-references)

---

## 0. Decisions taken

Settled 2026-09-22. Each replaces an open question; the reasoning that lost is kept so a later
reader knows it was weighed, not missed.

| # | Decision | Rationale | What it overrode |
|---|---|---|---|
| D-1 | **Ships in R1, not R2** | A red alert that reaches nobody is a defect, not a roadmap item. Beta families will be told CardiTrack watches their parent | My R2 recommendation |
| D-2 | **Absorbed into R1 — nothing removed** | R1 grows rather than displacing other work | The thinner-slice and date-move options |
| D-3 | **One builder — strictly serialised** | No parallelism with billing is available | — |
| D-4 | **Primary caregiver admits caregivers, every invite audited** | Matches Aloe Care and Bay Alarm; keeps the rule that the wearer is never an app user | Wearer approval (now OQ-3, R3 prototype) |
| D-5 | **Admin / Member roles built now** | Avoids migrating live grants later; matches the `family.md` contract | My recommendation to defer and use the three flags |
| D-6 | **Fan-out vs quiet hours is configurable, per receiving caregiver, evaluated in that caregiver's own `User.TimeZoneId`** | Quiet hours are already a per-user preference; the person whose sleep is at stake decides, and a cross-timezone sibling is judged by their own clock | A single account-level policy |
| D-7 | **Default: respect quiet hours** | Conservative default | Override-by-default (my recommendation) |
| D-8 | **Choice forced at invite acceptance** | Contains D-7: an accepting caregiver must decide whether escalated red alerts wake them, so the default rarely applies | Silent default |
| D-9 | **Real multi-org membership** — a `UserOrganization` join table; a user genuinely belongs to several families and keeps their own | A true roster, a clean "switch family" concept, and zero rows expresses "guest" naturally (D-12) | My grants-only recommendation |
| D-10 | **Admin picks which members a joiner gets, at approval** | HIPAA minimum-necessary; handles a family watching two parents where different siblings handle each | All-members-on-join |
| D-11 | **Both join paths: typed Family ID and 256-bit link** | A code you can read down the phone, and a safe link for everything else | Link-only (my recommendation) |
| D-12 | **A joiner gets no organization and no trial — they are a guest** | Cleanest expression of "only Admin pays"; the trial stops being burned by people who never asked for one | Always-create-an-org |
| D-13 | **An Admin leaves only by assigning another Admin** | The family never ends up unowned, and with "only Admin pays" the bill always has a named owner | — |

**Standing assumption from D-2 + D-3, recorded because it is not free:** one builder absorbing the
whole of this — ~3.3 person-months once D-9 to D-13 are counted — moves the R1 beta date by roughly
fourteen weeks. The date was not chosen as the thing to give, so it gives implicitly, and it has now
given twice. If that is not acceptable, the lever is D-2: Phase A (~1.1 pm) closes the safety gap on
its own, and Phases B and D can follow in R2 without reopening anything.

**Blast radius of D-9, measured rather than estimated (2026-09-22):** 62 `OrganizationId` references
in `src/` outside migrations — 23 Application, 21 Infrastructure, 9 API, 6 Domain, 3 Worker. Of the
six presentation call sites, five are in `OnboardingController` and one is the
`SetFullUserContext` line in `UserContextMiddleware`. Concentrated, not diffuse. An earlier draft of
this PRD warned that multi-org membership meant auditing usages "all over the code"; that was wrong
and is corrected here, because it priced the decision higher than it costs.

**Tension to watch (D-1 vs D-7):** most unacknowledged red alerts happen at night, which is exactly
when quiet hours apply, so respect-by-default switches the safety fix off in the hours it exists
for. D-8 is the containment. If beta ack-latency data shows fan-outs being suppressed at night,
D-7 is the first thing to revisit.

---

## 1. The Problem

Caregiving for an elderly parent is shared between siblings, but a CardiTrack account holds exactly
one caregiver — so the red-alert escalation ladder's fan-out rung has nobody to fan out to, and the
"up to 5 / 20 family members" on the pricing page cannot be delivered.

Alongside it, a second problem decided on 2026-09-22: a family is not the unit CardiTrack models.
A user belongs to exactly one organization, so joining a sibling's family means giving up their own.
Decisions D-9 to D-13 make family membership a many-to-many relation with a joinable Family ID, an
Admin who approves and pays, and a succession rule.

**Evidence — three independent sources, none of them a user-behaviour assumption:**

1. **The code says so.** `EscalationPolicy.FanOutToOtherCaregivers` — the t+300s rung for Safety and
   red Health deliveries — is reached and resolves to zero recipients, because
   `SubscriptionService` provisions `MaxUsers = 1` for a Family org. Its own doc comment:
   *"In R1 (`MaxUsers = 1`) this finds zero secondary caregivers and falls straight through."*
   An unacknowledged 3am red alert therefore escalates to nothing until it is marked undelivered at
   t+900s.
2. **The erasure cascade already assumes multiple watchers.** `AccountErasureService` erases the
   members a departing caregiver "was the last active watcher of" and *releases* the ones somebody
   else still watches. That branch cannot execute today.
3. **We already promise it.** `solution_manifest.md` §Pricing and the published pricing page sell
   5 / 20 / 20 family members. `BaselineLearningPage.xaml.cs` tells beta users they can
   "invite your family from the Family tab once it's live" — a tab that became CardiJournal.

The market-size claim (53M US / 44M EU informal carers, `market_analysis.md`) supports that care is
shared in general. What fraction of CardiTrack accounts will actually invite someone is
**[ASSUMPTION]** — see OQ-1.

## 2. Success Metrics

| Metric | Type | Baseline | Target | How measured |
|---|---|---|---|---|
| Red/Safety alerts acknowledged within 15 min | North Star | unknown — OQ-2 | +10pp vs single-caregiver accounts | `NotificationDelivery` stage + `Alert` ack timestamp |
| Accounts with ≥1 accepted caregiver invite at day 30 | Leading (activation) | 0 (impossible today) | 35% | `CaregiverInvite.Status = Resolved` per org |
| Escalations reaching `FannedOut` that are then acknowledged | Leading (efficacy) | 0 | >50% of fan-outs | `EscalationStage` transitions |
| Invites sent but never opened | Guardrail | — | <30% | `CaregiverInvite.OpenedAt IS NULL` past expiry |
| Secondary-caregiver push opt-outs / mutes | Guardrail | — | <15% | `NotificationPreference` per-category mute |
| Accounts with zero night coverage (every caregiver holding for quiet hours) | Guardrail | — | <25% | Fan-out preference × quiet-hours window per account |
| Fan-outs suppressed by quiet hours | Guardrail | — | tracked, no target yet | Escalation rung recorded as attempted-but-suppressed |

Ladders to the committed churn (<5%/mo) and alert-latency KPIs. The ack-latency North Star is
measurable today from existing `NotificationDelivery` and `Alert` rows; the invite metrics need the
new entity. Nothing here requires new instrumentation beyond DB queries —
see [apm_setup_runbook.md](../technical/apm_setup_runbook.md).

## 3. Out of Scope

- **Shared care notes + @mentions** — deferred to **R3**. RICE 6.3 vs 80 for this slice; also the
  most crowded part of the competitive field (see §4).
- **`Staff` role and anything facility-facing** — **out.** `UserRole.Staff` stays unused by Family
  orgs; it belongs to the Enterprise offering, which is post-R4 and recruiting design partners.
  Admin / Member ship here (D-5).
- **Activity/audit log *read* endpoint** — deferred to **R3**, though it sits on the RICE cut line
  (§10). The audit *write* side ships here (`[AuditHealthDataAccess]`), which is the
  compliance-relevant half.
- **Multi-member comparison views** — deferred to **R3**, unrelated to this problem.
- **Plan-gating family-member count** — **dropped as a tier lever.** No competitor charges per
  caregiver seat (§4). The tier limit is enforced as a ceiling, never sold as a feature.
- **Wearer-approved caregiver admission** — **prototype first, not in this slice.** See OQ-3; it is
  the strongest answer to the consent risk but needs per-metric consent recording, which is
  ⬜ not started.
- **Switching the "active" family in the UI** — **out of this PRD.** D-9 makes multi-family
  membership real in the data model; a family switcher on the mobile shell is a navigation design
  problem with no Figma frame and no agreed shape. Until it exists, a user sees every member they
  have a grant for, in one list, regardless of which family owns them — which is what the
  link-based access path already does.
- **Guest-to-owner upgrades beyond the lazy path** — **out.** A guest who adds their first
  CardiMember gets a family and a trial (D-12). Anything more elaborate (claiming a family,
  merging two families, moving a member between families) is not in scope and has no agreed
  behaviour.
- **SMS/email invitations** — **dropped.** SMS is permanently out of scope
  ([solution_manifest.md](../solution_manifest.md)); the caregiver shares the link through their own
  phone's share sheet, exactly as `DeviceConnectionInvite` already does, so we never learn who they
  sent it to.

## 4. Competitive Position

Checked 2026-09-22; `market_analysis.md`'s teardowns compare monitoring features and predate this.

| Product | Caregiver seats | Price of a seat | Roles | Who grants access |
|---|---|---|---|---|
| Aloe Care Health | **Unlimited** (recommends 5) | **Free** — unlimited logins per account | **Four access levels** | Account admin |
| Bay Alarm Medical | Multiple | **Free** — whole caregiver app free | None published | Account admin |
| Medical Guardian | Multiple | App free; **$2.99/mo** for emergency notifications to loved ones | None published | Account admin |
| Apple Health Sharing | **Up to 5** | Free | None | **The wearer** — per data type, revocable anytime |
| Fitbit Premium / Garmin Connect | — | — | — | No family monitoring at all |

**Three conclusions that change the plan:**

1. **Caregiver seats are free everywhere.** Aloe Care gives unlimited logins at no per-seat charge;
   Bay Alarm's caregiver app is entirely free. This confirms family sharing is not a pricing lever —
   and goes further: **the "up to 5 family members" line on Basic is a competitive liability**, since
   the market norm is unlimited. Enforce the ceiling quietly; stop selling it. Note Medical
   Guardian monetises the *notification*, not the seat — the closest thing to a viable upsell here,
   and CardiTrack's push spine already does it for free.
2. **Aloe Care has four access levels, and we are building two (D-5).** I had argued for deferring
   roles, on the grounds that their levels gate *two-way voice to a Smart Hub* — hardware
   CardiTrack does not have — and that their circle mixes paid professionals with family. The
   decision went the other way, and the strongest argument for it is not competitive but
   practical: a role assigned at invite time costs nothing, while retrofitting one onto live grants
   is a migration. We stop at Admin / Member; the remaining levels stay an Enterprise concern.
3. **Apple's model is wearer-granted, per-metric, revocable — and CardiTrack's is not.** Apple caps
   at 5 people and lets recipients see the wearer's own high-heart-rate and irregular-rhythm
   notifications plus significant-trend alerts. That is the precedent for OQ-3, and the sharpest
   privacy contrast a reviewer will draw: on CardiTrack the wearer neither grants nor sees who
   watches them.

**The wedge:** the coordination field is crowded (Caring Village, Caily, Medisafe shared profiles),
and industry reviews say those apps "underdeliver on real multi-person coordination". But nobody in
either camp does **escalating alert fan-out** — the alert companies have one recipient or a flat
broadcast; the coordination apps have no alerts. CardiTrack's ladder (push → re-push at t+120s →
fan out at t+300s → page at t+900s) is already built. This slice is what switches it on, and it is
defensible precisely because it is not a notes feature.

## 5. User Stories & Acceptance Criteria

**Story 4.1: Inviting a second caregiver** _(P0 — Must Have)_
- **As a** primary caregiver
- **I want to** invite my sibling to watch our mother with me
- **So that** an alert I miss still reaches someone
- **Acceptance Criteria:**
  - **Given** I am an Admin on the account **When** I open "Who can see Margaret" and create an
    invite **Then** I choose their role (Admin or Member) and what they get (view health data,
    receive alerts), and receive a share link and QR code, with the invite token shown exactly once
  - **Given** the role is chosen at invite time **Then** it is carried on the invite row and applied
    at redemption, so no live grant is ever migrated to acquire a role (D-5)
  - **Given** an invite exists **When** I view the list **Then** I see its state (pending / opened /
    accepted / expired) and can revoke it
  - **Given** I am a Member, not an Admin **When** I try to invite **Then** the endpoint refuses
    with the same "CardiMember not found" message used for a member that does not exist, so a
    non-admin cannot enumerate members by probing
  - **[Edge]** **Given** an invite past `ExpiresAt` **When** the recipient opens it **Then** it is
    dead regardless of stored status, and the page offers no member's name
  - **[Edge]** **Given** the inviter's own access was revoked after issuing **When** the recipient
    redeems **Then** redemption fails — an invite must not outlive the authority that issued it
  - **[Edge]** **Given** the account is at its tier's family-member ceiling **When** I invite
    **Then** `MEMBER_LIMIT_REACHED` (422) names the current count and the ceiling
- **Screens:** no Figma M1 frame — **needs design sync** (new "Who can see <member>" screen on
  member detail, plus an invite sheet)
- **API:** new — `POST|GET /api/v1/cardimembers/{id}/caregiver-invites`,
  `DELETE .../caregiver-invites/{inviteId}`; see [family.md](../execution/backend/api/family.md)
- **Wave:** R1 · **Plan gate:** none (ceiling enforced)

**Story 4.2: Accepting an invitation** _(P0 — Must Have)_
- **As an** invited family member
- **I want to** open my sibling's link and start seeing Mum's status
- **So that** I can take my share of the watching
- **Acceptance Criteria:**
  - **Given** a valid invite link **When** I open it **Then** I see who invited me and which member,
    first name only, read from the member record at render time — never copied into the invite
  - **Given** I have no account **When** I accept **Then** I sign up through Auth0, verify email, and
    only then is a `UserCardiMember` link created with the flags the inviter granted
  - **Given** I accept **Then** I must choose, before the flow completes, whether an escalated red
    alert wakes me inside my quiet hours — the choice is required, not defaulted (D-8), and is
    stored against my own user, evaluated in my own `User.TimeZoneId` (D-6)
  - **Given** I skip that step by backing out and returning **Then** the stored value is "respect
    quiet hours" (D-7), and the caregiver list shows my night coverage as off
  - **Given** I accept **Then** `OpenedAt`/`ResolvedAt` are recorded, my role is applied from the
    invite, and the inviter sees the state change
  - **[Edge]** **Given** I hold only the invite token and am not authenticated **When** I request any
    health data **Then** I am refused — the token authorizes creating the link, never reading PHI
  - **[Edge]** **Given** I already have access to this member **When** I redeem **Then** the invite
    resolves without creating a duplicate link, and my existing flags are not silently widened
  - **[Edge]** **Given** the member's baseline is still learning (days 1–14) **When** I first open
    the dashboard **Then** I see the same learning-progress state the primary caregiver sees, not an
    empty dashboard that reads as "healthy"
- **Screens:** no Figma M1 frame — **needs design sync**
- **API:** new — `GET|POST /api/v1/caregiver-invites/{token}` (anonymous view, authenticated redeem)
- **Wave:** R1 · **Plan gate:** none

**Story 4.3: An alert nobody answered reaches the second caregiver** _(P0 — Must Have)_
- **As a** family sharing the watching
- **I want to** have an unanswered red alert passed to whoever else is there
- **So that** a phone face-down at 3am is not the end of the chain
- **Acceptance Criteria:**
  - **Given** a red or Safety alert unacknowledged at t+300s **When** the escalation sweep runs
    **Then** every other active caregiver with `ReceiveAlerts` gets one delivery, deduped per
    recipient
  - **Given** a fan-out copy **Then** it never names who failed to respond (§6.3
    [notification_engine.md](../technical/notification_engine.md))
  - **[Edge]** **Given** the second caregiver is inside their quiet hours **in their own timezone**
    **When** fan-out fires **Then** their own stored preference decides: pierce, or hold and let the
    ladder continue. Quiet hours are evaluated against `User.TimeZoneId` of the *recipient*, never
    the member's anchor timezone — a sibling three timezones away is judged by their own clock (D-6)
  - **[Edge]** **Given** every other caregiver is holding for quiet hours **When** fan-out fires
    **Then** the rung is recorded as attempted-but-suppressed rather than delivered, so the ladder
    advances to undelivered-critical on schedule and the suppression is visible in the data
  - **[Edge]** **Given** two caregivers acknowledge within seconds **When** both writes land
    **Then** the first wins, the second sees "Tom acknowledged this 2 minutes ago", and no
    acknowledgement is lost
  - **[Edge]** **Given** the second caregiver has muted that alert category **When** fan-out fires
    **Then** their mute is honoured and the ladder continues to the next rung rather than counting
    them as reached
- **Screens:** M1-10 (Alerts), M1-11/12/16 (`AlertDetailPage`) — ack attribution line is new copy
- **API:** existing — `AlertsController` ack/undo-ack; `IDispatchService` fan-out
- **Wave:** R1 · **Plan gate:** none

**Story 4.4: Removing a caregiver** _(P1 — Should Have)_
- **As a** primary caregiver
- **I want to** remove someone's access and know exactly what that did
- **So that** access matches who is actually caring
- **Acceptance Criteria:**
  - **Given** another caregiver has access **When** I remove them **Then** their link is deactivated,
    they lose the dashboard immediately, and they stop being a fan-out target
  - **[Edge]** **Given** I remove the only other caregiver **When** the removal lands **Then** the
    member is not left unwatched without telling me, and monitoring continues under me
  - **[Edge]** **Given** a removed caregiver **Then** their audit rows and delivery history are
    retained (6-year audit retention), and the UI says so rather than implying erasure
  - **[Edge]** **Given** the removed caregiver was the *last* active watcher of some other member
    **When** they later delete their own account **Then** `AccountErasureService` erases what they
    last watched and releases what others still watch — the branch this slice finally exercises
- **Screens:** no Figma M1 frame — **needs design sync**
- **API:** new — `DELETE /api/v1/cardimembers/{id}/caregivers/{userId}`
- **Wave:** R1 · **Plan gate:** none

**Story 4.5: Account roles** _(P1 — Should Have)_
- **As an** account Admin
- **I want to** control who else can invite people and change settings
- **So that** adding a sibling to watch Mum does not also hand them the account
- **Acceptance Criteria:**
  - **Given** the existing `UserRole` enum (Member=1, Admin=2, Staff=3) **Then** roles are
    **account-level on `User.Role`**, not a second per-member axis — per-member data access stays
    with the three `UserCardiMember` flags, so there is one place to look for "can they see this"
    and one for "can they change that"
  - **Given** onboarding creates the first caregiver **Then** they are Admin, and the account always
    has at least one
  - **Given** I am the only Admin **When** I try to demote myself **Then** `CANNOT_DEMOTE_LAST_ADMIN`
    (422), and the same rule blocks removing the last Admin
  - **[Edge]** **Given** a Member **When** they call invite, remove-caregiver or role-change
    **Then** they are refused, and the refusal is audited like any other access decision
  - **[Edge]** **Given** roles ship in the same release as the first invites **Then** no live grant
    is migrated — every account has exactly one Admin from onboarding, and every later caregiver
    arrives carrying a role from their invite (D-5)
  - **[Edge]** **Given** a Family org **Then** `Staff` is never assignable; it stays an Enterprise
    concern
- **Screens:** no Figma M1 frame — **needs design sync** (role selector in the invite sheet; role
  shown on the caregiver list)
- **API:** new — `PUT /api/v1/family-members/{userId}/role`; see
  [family.md](../execution/backend/api/family.md), whose `viewer` role does not exist and must go
- **Wave:** R1 · **Plan gate:** none

**Story 4.6: Starting or joining a family at onboarding** _(P0 — Must Have)_
- **As a** new user
- **I want to** either name my own family or join one that already exists
- **So that** I am not forced to duplicate a family my sibling already set up
- **Acceptance Criteria:**
  - **Given** onboarding **When** I reach the family step **Then** I can name a new family, enter a
    Family ID, or arrive with one already filled in from a link I tapped
  - **Given** I name a new family **Then** an `Organization`, a trial subscription and my Admin
    membership are created in one transaction, exactly as today
  - **Given** I join an existing family **Then** **no organization and no subscription are created
    for me** (D-12) — I am a guest until I create a family of my own
  - **Given** I am a guest **When** I later add a CardiMember of my own **Then** my own family and
    its trial are created at that moment, and my trial starts when it means something
  - **[Edge]** **Given** `User.OrganizationId` is non-nullable today and onboarding is one atomic
    transaction **Then** the join path requires that invariant to change — membership moves to
    `UserOrganization` rows (D-9), and zero rows is the guest state
  - **[Edge]** **Given** a retried onboarding after a lost response **When** it replays **Then** it
    returns the existing account rather than creating a second family, as `SetupAsync` already does
- **Screens:** no Figma M1 frame — **needs design sync** (family step is new; M1-04 follows it)
- **API:** `POST /api/v1/onboarding/setup` gains a join branch
- **Wave:** R1 · **Plan gate:** none

**Story 4.7: Asking to join, and being approved** _(P0 — Must Have)_
- **As an** Admin
- **I want to** approve who joins my family and choose what they can see
- **So that** knowing a Family ID is never the same as having access
- **Acceptance Criteria:**
  - **Given** someone submits a Family ID or opens a join link **Then** a join request is created
    and they are told only that it was sent
  - **Given** a join request **When** I review it **Then** I see who is asking, and I choose which
    CardiMembers they get and their role before approving (D-10)
  - **Given** I approve **Then** a `UserOrganization` row and one `UserCardiMember` grant per chosen
    member are created together, and the requester is notified
  - **[Edge]** **Given** a typed Family ID that does not exist **Then** the response is
    indistinguishable from one that does — no family name, no member names, no existence signal —
    and repeated attempts are rate-limited per user and per IP
  - **[Edge]** **Given** a typed Family ID is short enough to brute-force by construction (D-11)
    **Then** approval is the control that makes guessing worthless, and that is written down as a
    security property rather than a product preference
  - **[Edge]** **Given** a pending request **When** the Admin never acts **Then** it expires, and the
    requester can ask again without the Admin seeing a duplicate queue
  - **[Edge]** **Given** the family is at its tier's family-member ceiling **When** I approve
    **Then** `MEMBER_LIMIT_REACHED` (422) names the count and the ceiling
- **Screens:** no Figma M1 frame — **needs design sync** (join request queue; approval sheet with
  member picker)
- **API:** new — `POST /api/v1/families/{familyId}/join-requests`,
  `GET|POST /api/v1/families/{familyId}/join-requests/{id}/approve`
- **Wave:** R1 · **Plan gate:** none

**Story 4.8: An Admin handing the family on** _(P1 — Should Have)_
- **As an** Admin who no longer wants to run the family
- **I want to** hand Admin to someone else and leave
- **So that** the family is never left unowned
- **Acceptance Criteria:**
  - **Given** I am the only Admin **When** I try to leave **Then** I am refused until I promote
    someone (D-13), consistent with `CANNOT_DEMOTE_LAST_ADMIN`
  - **Given** I promote another member and leave **Then** they become Admin, my `UserOrganization`
    row and my grants for that family's members are removed, and the handover is audited
  - **[Edge]** **Given** "only Admin pays" **When** Admin changes **Then** the successor must
    *accept*, not merely be nominated — in R1 this is a role change, but in R2 it transfers who owes
    money, and an R1 flow that makes it look free would mislead once Stripe lands
  - **[Edge]** **Given** I am the only person in the family **When** I leave **Then** there is no
    successor and this is account deletion, routed to the existing erasure flow rather than a
    silent orphaning
  - **[Edge]** **Given** I leave a family where I was the last active watcher of a member **Then**
    `AccountErasureService`'s existing release-or-erase branch decides that member's fate
- **Screens:** no Figma M1 frame — **needs design sync**
- **API:** new — `PUT /api/v1/families/{familyId}/admin`, `DELETE /api/v1/families/{familyId}/members/me`
- **Wave:** R1 · **Plan gate:** none

## 6. Open Questions

| # | Question | Owner | Blocks | Needed by |
|---|---|---|---|---|
| OQ-1 | What share of accounts invite ≥1 caregiver in 30 days? Currently unmeasurable — nobody can. | Product | Whether R3's coordination half is worth building | R2 beta + 30 days |
| OQ-2 | Baseline ack-latency for red/Safety alerts on single-caregiver accounts | Product + Eng | The North Star baseline | Before Phase A starts |
| OQ-3 | Can the wearer admit a caregiver via the link/QR pattern, as Apple's model does? Needs per-metric consent recording (⬜ not started) | Legal/DPO + Product | Nothing in this slice — it is the follow-on prototype | R3 planning |
| OQ-6 | Is prod erasure out of rehearsal? `retention_worker_dry_run = true` in dev; prod has none of it | Eng | **Prod** release of this slice, not dev | Before prod enablement |
| OQ-8 | Does beta ack-latency show fan-outs being suppressed at night? If so, revisit D-7 | Product | Whether respect-by-default survives | Beta + 30 days |
| OQ-9 | Which org's tier governs a member's features when the viewer belongs to several families? Proposed: the org that **owns the CardiMember**, never the viewer's | Product + Eng | Plan enforcement, whenever it lands | Before D3 |
| OQ-10 | What shape is a typed Family ID — length, alphabet, checksum? Must be non-sequential and rate-limited; shorter is friendlier and weaker | Eng + Security | D2 | Before D2 |
| OQ-11 | Does `UserContext` need an active-org concept, or can every call be member-scoped? Five of six call sites are in `OnboardingController`, which suggests member-scoping is enough | Eng | D1 | Before D1 |
| OQ-12 | On Admin succession, how does the successor accept once Stripe exists — and what happens to the subscription mid-period? | Product + Eng | D5 in R2 terms | R2 billing |
| OQ-7 | Does Google restricted-scope verification change the reach ceiling? Still ⬜ not started as of the last matrix read | Eng/Ops | Reach in §9 | R1→R2 gate |

## 7. Risk & Dependency Check

| Risk | Assessment | Severity | Evidence needed to de-risk |
|---|---|---|---|
| **Value** | Not a demand bet: the escalation rung and the erasure release-branch are both built and unreachable, and the pricing page already sells this. The unknown is only *how many* invite (OQ-1). Competitors treating seats as free and unlimited confirms it is table stakes, not differentiation | 🟢 | OQ-1 after 30 beta days |
| **Usability** | The invite is easy; explaining the grant is not. A 45–65 caregiver must be able to state what their sibling will see. Aloe Care needed four access levels to make this legible at scale — we are betting three booleans suffice for a small family circle | 🟠 | Prototype the grant screen; 5 caregivers state correctly what the invitee sees |
| **Feasibility** | Cheapest major feature left. `DeviceConnectionInvite` (#1131–#1137) is a shipped, reviewed invitation primitive to mirror; `CardiMemberAccessService`, the push spine, the ladder and the audit middleware all exist. The one genuinely new piece is redemption ending in an authenticated account rather than anonymously | 🟢 | — |
| **Security** | New with D-11. A typed Family ID is brute-forceable by construction, so mandatory approval, per-user and per-IP rate limiting, and a response that never confirms a family's existence are all load-bearing. The link path is unguessable and carries no such burden | 🟠 | Threat-model the join endpoint before D2 ships; `security-architect` review of the enumeration surface |
| **Feasibility (D-9)** | Measured, not guessed: 62 `OrganizationId` references outside migrations, nine in the API, five of those in `OnboardingController`. Concentrated and mechanical | 🟢 | — |
| **Viability** | Was 🔴 on 2026-09-05 because erasure was unbuilt while the privacy policy promised 30-day deletion. Erasure shipped 2026-09-14 (#1088/#1089/#1090/#1094) with a tested cascade and upstream OAuth revocation — **but dev-only and in rehearsal**. Widening PHI access to a second human is defensible once erasure actually runs in prod, and not before. The residual gap is that the wearer still neither grants nor sees who watches them, where Apple's comparable feature is wearer-granted | 🟠 | OQ-6 (prod erasure live) + OQ-3 direction |

**Verdict: Pursue — R1, as the five stories above (D-1).** The R3 row "Family invitations + roles"
splits: invitations, roles, enforcement and fan-out activation move to **R1**, treated as closing a
safety defect before beta families rely on alerts. Shared notes, @mentions, the audit-log read
endpoint and multi-member comparison stay in R3.

**Added risk from D-2 + D-3 — schedule:** one builder absorbing ~1.7 person-months into R1 with
nothing removed moves the beta date by roughly seven weeks. Severity 🟠. The de-risking lever is
named in §0: ship Phase A only (~1.0 pm) and let Phase B fall into R2.

**Standing constraints touched:** 100-wearer cap (bounds Reach, not this feature — invites add
*users*, not wearers, so this is the one growth lever the cap does not block) · no billing until R2
(the family-member ceiling is enforced in R1 with no billing behind it, which is fine because nothing
is gated — it is a ceiling, not a paywall) · HIPAA/GDPR consent + minimum-necessary (per-member grants,
not org-wide) · not-a-medical-device (unchanged — no new inference).
**Not touched:** Fitbit sunset (confirmed no exposure 2026-09-05), mobile disclosure gap.

**Dependencies (build status as of 2026-09-22):**

| Depends on | Status |
|---|---|
| `DeviceConnectionInvite` + `WearerConnectController` — the pattern to mirror | ✅ Shipped |
| `CardiMemberAccessService.RequireManageAccessAsync` — gates who may invite | ✅ Shipped |
| Push spine + `EscalationPolicy` ladder | ✅ Shipped (fan-out rung inert) |
| `AuditLoggingMiddleware` + `[AuditHealthDataAccess]` | ✅ Shipped |
| Account/member erasure cascade | 🟩 Live in dev, **rehearsal**; prod none — OQ-6 |
| `MaxUsers` / `MaxCardiMembers` enforcement | ⬜ Not started — **in this slice** |
| Consent recording (per-metric) | ⬜ Not started — blocks OQ-3 only |
| Stripe billing | ⬜ Not started — irrelevant; nothing is gated |

## 8. Build Order (one builder, serialised)

D-3 means no parallelism: this is a queue, not a board. Phase A is the whole safety argument — the
gap is closed at the end of it and not before, because fan-out needs a second caregiver to exist.

| # | Phase | Work | Effort | Gap closed? |
|---|---|---|---|---|
| 0 | Now | Fix `BaselineLearningPage.xaml.cs:57` — it points beta users at a Family tab that became CardiJournal | minutes | no |
| A1 | Safety | `CaregiverInvite` entity + migration, mirroring `DeviceConnectionInvite`; role carried on the row | 0.25 pm | no |
| A2 | Safety | Invite issue / list / revoke endpoints, Admin-gated, audited | 0.25 pm | no |
| A3 | Safety | Accept flow: Auth0 → link creation, role applied, **forced quiet-hours choice** (D-8) | 0.35 pm | no |
| A4 | Safety | Fan-out activation: recipient resolution, per-recipient quiet-hours evaluation in `User.TimeZoneId` (D-6/D-7), suppressed-rung recording, ack attribution | 0.25 pm | **yes — end of Phase A** |
| B1 | Correctness | `User.Role` Admin/Member enforcement, `CANNOT_DEMOTE_LAST_ADMIN` | 0.25 pm | — |
| B2 | Correctness | `MaxUsers` + `MaxCardiMembers` enforcement, one pass | 0.15 pm | — |
| B3 | Correctness | Caregiver list, removal, night-coverage line | 0.25 pm | — |
| D1 | Family | `UserOrganization` join table; `User.OrganizationId` retired into it; active-org resolution in `UserContextMiddleware`; the nine API references corrected (D-9) | 0.5 pm | — |
| D2 | Family | Family ID (non-sequential) + 256-bit join link; join-request entity; rate limiting and a non-confirming response (D-11) | 0.35 pm | — |
| D3 | Family | Approval queue with member picker and role (D-10) | 0.3 pm | — |
| D4 | Family | Onboarding fork: name a family, or join as a guest with no org and no trial; lazy org creation on first member (D-12) | 0.35 pm | — |
| D5 | Family | Admin succession with explicit acceptance; leave-family; solo-Admin routed to erasure (D-13) | 0.2 pm | — |
| C | Docs | Correct `family.md` (drop `viewer`), DPIA lines for `CaregiverInvites`, `UserOrganization` and join requests, notification-engine §6.3 | 0.15 pm | — |

**Total ≈ 3.3 person-months** — 1.7 for the caregiver slice, 1.7 for the family model (D-9…D-13),
0.15 docs. For one builder that is roughly fourteen weeks.

**Two cut lines, not one.** After **A4** the safety gap is closed — that is the ~1.1 pm that
justifies being in R1 at all. After **B3** the caregiver slice is complete and coherent without any
of the family-model work. Phase D is a separate product decision that happens to have been taken at
the same time; it can move to R2 whole without leaving anything half-built, because a caregiver
invited by link needs none of it.

**Sequencing constraint:** D1 should land *before* A3, or not at all in R1. The accept flow writes
membership, so if `UserOrganization` is coming, it should exist before the first live grant is
written — otherwise D1 becomes a data migration of real families rather than a schema change to an
empty table. This is the same argument that decided roles in D-5.

## 9. Surface & Release Placement

- **API:** new `CaregiverInvitesController` + `ICaregiverInviteService`, mirroring
  `DeviceInvitesController` / `IDeviceConnectionInviteService`. Extends
  [family.md](../execution/backend/api/family.md), which must be corrected: it specifies org-scoped
  `/api/v1/family-members` with a `viewer` role that does not exist in the `UserRole` enum. The
  implemented primitive is the per-member `UserCardiMember` link, and this PRD keeps it.
- **Mobile:** new "Who can see <member>" screen off member detail, plus invite sheet and accept
  flow. **No Figma M1 frames — needs design sync** (adds to the seven-screen backlog).
- **Web:** not planned this wave (web is still template-stage).
- **Worker:** no new job. Fan-out rides the existing escalation sweep; invite expiry is a timestamp
  check, not a sweep. Any future expired-invite cleanup belongs in `CardiTrack.Worker` only.
- **Data:** new `CaregiverInvites` table — **identifier schema**, no clinical fields. Holds two ids,
  granted flags, a SHA-256 token hash (never the token), and timestamps. No invitee name, email or
  phone: the caregiver addresses the message themselves. Needs a DPIA processing-inventory line
  ([dpia.md](../compliance/dpia.md)) and inclusion in the erasure cascade's `SubjectDataMap`.

## 10. RICE

| Item | Reach (users/qtr) | Impact | Confidence | Effort (pm) | RICE | Wave | Notes |
|---|---|---|---|---|---|---|---|
| Second-caregiver slice (Phases A+B) | 50 | 2 | 80% | 1.7 | **47** | R1 | 1.0 base; +0.4 roles (D-5), +0.2 configurable fan-out (D-6/7), +0.1 forced choice (D-8) |
| Family model — multi-org, join, approval, succession (Phase D) | 50 | 1 | 50% | 1.7 | **15** | R1 by decision | Scores like the deferred R3 items, and is in R1 by product choice rather than by score. Recorded, not contested |
| — of which Phase A alone (closes the gap) | 50 | 2 | 80% | 1.1 | **73** | R1 | The cut line if the R1 date must hold |
| Fan-out activation (Story 4.3) | 50 | 2 | 80% | 0.25 | **320** | R1 | Only scoreable *after* A1–A3; not independent |
| Shared notes + @mentions | 50 | 1 | 50% | 2.0 | **12.5** | R3 | Crowded field (§4) |
| Roles — Admin/Member only | 50 | 0.5 | 80% | 0.4 | **50** | R1 | Decided in (D-5). Scores above the cut line at this narrower scope and higher confidence — the full four-level version did not |
| Audit-log read endpoint | 50 | 0.5 | 80% | 0.5 | **40** | R3 | Write side ships in R1 with the slice; Admin-facing read is the remainder |
| Multi-member comparison | 25 | 0.5 | 50% | 1.5 | **4.2** | R3 | Reach halved — needs ≥2 CardiMembers |

Reach is 50 accounts/quarter, capped by the 100-connected-wearer ceiling until Google restricted-scope
verification passes (OQ-7), not by demand. Confidence is 80% where the claim is verifiable in code
and 50% for anything resting on unvalidated caregiver behaviour.

**Cut line: RICE 40.** Above it and shipping in R1: the slice, fan-out activation, and
Admin/Member roles. Below it and staying in R3: shared notes, multi-member comparison. The
audit-log read endpoint sits exactly on the line — cheap, and its write half ships here anyway — so
it is the first candidate if R1 has room, but it is not committed scope.

Two honest notes on these numbers. First, roles score **50** here against **12.5** in the previous
revision: the scope narrowed from four access levels to two, effort fell from 1.0 to 0.4, and
confidence rose from 50% to 80% once the claim became "avoids migrating live grants" (verifiable)
rather than "families want access levels" (a behavioural guess). The decision changed the score, not
the other way round — but it does mean my earlier "defer roles" ranking was scoring a bigger feature
than the one now being built. Second, the whole slice scores **47**, below Phase A alone at **73**,
which is what a cut line is for: if the R1 date has to hold, everything after A4 is the part to
move.

## 11. References

- [release_matrix.md](../release_matrix.md) — canonical waves; the R3 rows this PRD splits
- [solution_manifest.md](../solution_manifest.md) — pricing tiers, unit economics
- [market_analysis.md](../market_analysis.md) — segments and competitor teardowns (§4 above
  supersedes its family-sharing lines, checked 2026-09-22)
- [family.md](../execution/backend/api/family.md) — planned contract; needs the corrections in §8
- [notification_engine.md](../technical/notification_engine.md) — escalation ladder §6.3
- [data_protection_architecture.md](../technical/data_protection_architecture.md) — schema
  separation, minimum-necessary
- [dpia.md](../compliance/dpia.md) — needs a processing line for `CaregiverInvites`
- [mobile user_stories.md](../execution/ui/mobile/user_stories.md) — canonical Stories 4.1/4.2 that
  this PRD supersedes for R2 scope
