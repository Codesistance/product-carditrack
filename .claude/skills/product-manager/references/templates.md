# Templates

Copy-paste starting points. Delete sections that genuinely don't apply — but say which and why; a silently missing Out of Scope reads as "unbounded".

---

## 1. PRD

```markdown
# PRD: <feature name>

**Surfaces:** API / Mobile / Web · **Wave:** R1 | R2 | R3 | R4 · **Plan gate:** none | Basic | Complete Care | Guardian Plus
**Status:** Draft | In review | Approved · **Author:** · **Last updated:** <YYYY-MM-DD>

## 1. The Problem
<1–2 punchy sentences on the verified user pain. Name the segment. Mark [ASSUMPTION] if unvalidated
and put the validation plan in Open Questions.>

**Evidence:** <link to market_analysis segment, support volume, beta feedback, or "none — see OQ-1">

## 2. Success Metrics
| Metric | Type | Baseline | Target | How measured |
|---|---|---|---|---|
| <north star> | North Star | <or "unknown — OQ-n"> | | <APM signal / DB query / analytics event> |
| <leading indicator> | Leading | | | |
| <counter-metric> | Guardrail | | | |

Ladder these to the committed KPIs (false-positive rate <10%, alert latency <30s, trial→paid >20%,
churn <5%, CAC <$50). If a metric has no measurement path in
[apm_setup_runbook.md](../../../../docs/technical/apm_setup_runbook.md), it is not yet a metric.

## 3. Out of Scope
- <thing> — **deferred to R<n>** because <reason>
- <thing> — **dropped** because <reason>

## 4. User Stories & Acceptance Criteria
<see the story template below — every story needs ≥1 error/edge path>

## 5. Open Questions
| # | Question | Owner | Blocks | Needed by |
|---|---|---|---|---|
| OQ-1 | | Eng / Design / Legal / Product | | |

## 6. Risk & Dependency Check
| Risk | Assessment | Severity | Evidence needed to de-risk |
|---|---|---|---|
| Value | | 🔴/🟠/🟢 | |
| Usability | | | |
| Feasibility | | | |
| Viability | | | |

**Verdict:** Pursue / Prototype first / Kill — <one line>

**Standing constraints touched:** <100-wearer cap · Fitbit sunset · no billing until R2 · HIPAA/GDPR
schema + consent · not-a-medical-device · mobile disclosure gap — or "none">

**Dependencies:** <shipped things this relies on, with build status from the release matrix>

## 7. Surface & Release Placement
- **API:** <endpoints touched — link the domain doc; new vs existing>
- **Mobile:** <M1-nn frames, or "needs design sync — no frame">
- **Web:** <screens, or "not planned this wave">
- **Worker:** <background job needed? it can only live in CardiTrack.Worker>
- **Data:** <new fields → identifier or clinical schema? consent? retention? DPIA line needed?>
```

---

## 2. User story — house style

Match the existing specs ([mobile user stories](../../../../docs/execution/ui/mobile/user_stories.md)) so new stories drop straight in.

```markdown
**Story <n.n>: <Short title>** _(P0 — Must Have)_
- **As a** <segment: family caregiver / CardiMember / family admin>
- **I want to** <capability>
- **So that** <outcome that matters to them>
- **Acceptance Criteria:**
  - **Given** <context> **When** <action> **Then** <observable result>
  - **Given** <context> **When** <action> **Then** <observable result>
  - **[Edge]** **Given** <failure/empty/expired/offline state> **When** <action> **Then** <defined behaviour>
  - **[Edge]** **Given** <plan gate or consent boundary> **When** <action> **Then** <defined behaviour>
- **Screens:** M1-nn (<name>) — or "no Figma M1 frame — needs design sync"
- **API:** <endpoint(s)> — link the domain doc
- **Wave:** R<n> · **Plan gate:** <none | Complete Care | …>
```

Priority tags follow the existing convention: `_(P0 — Must Have)_`, `_(P1 — Should Have)_`, `_(P2 — Could Have)_`, and priorities are **relative to the wave the story ships in**.

### Edge-path prompts

Pick the ones that bite; don't pad. Data absence (watch off, battery dead, poll returned nothing — silence must never render as "healthy") · OAuth revoked or scope changed · false alarm and its tuning path · baseline still learning (days 1–14) · consent withdrawn / GDPR erasure mid-subscription · CardiMember has no login and didn't consent · trial expiry on day 31 with no billing · plan-gate collision for a lower-tier family member · caregiver and wearer in different timezones, 3am critical alert · two caregivers acting on the same alert · accessibility for a 70+ wearer and a 45–65 caregiver · offline mobile.

---

## 3. RICE prioritisation

Show inputs or it isn't a score.

```markdown
| Item | Reach (users/qtr) | Impact (3/2/1/.5/.25) | Confidence (100/80/50%) | Effort (person-months) | RICE | Wave | Notes |
|---|---|---|---|---|---|---|---|
| | | | | | R×I×C/E | | |
```

Reach reality-check: connected wearers are **capped at 100** until Google restricted-scope verification passes, and R1 monitors **one CardiMember per account**. A reach number above that needs the assumption written down. Confidence is 50% for anything resting on unvalidated caregiver behaviour.

After scoring, state the cut line and what falls below it. A ranked list with no cut line hasn't prioritised anything.

---

## 4. Opportunity assessment (pre-PRD, ~1 page)

Use before committing to a PRD — it's the cheapest place to kill something.

```markdown
# Opportunity: <name>

1. **What problem, for whom?** <segment + pain, one line>
2. **How do we know it's real?** <evidence, or "we don't — this is the first thing to test">
3. **How big?** <reach × frequency; honest bounds given the 100-wearer cap>
4. **Why us, why now?** <differentiation vs the competitors in market_analysis.md; what changed>
5. **What would have to be true?** <the 2–3 riskiest assumptions, most-fatal first>
6. **Cheapest test of the riskiest assumption** <prototype, fake door, 5 caregiver interviews, data query>
7. **4 Big Risks** <table — Value / Usability / Feasibility / Viability with a verdict each>
8. **Recommendation:** Pursue now (wave R<n>) / Prototype first / Kill — <one line of reasoning>
```

---

## 5. Scope-cut memo

When something must fit a wave and doesn't.

```markdown
# Scope cut: <feature> for R<n>

**Non-negotiable:** <the wave date or external gate forcing the cut>
**Keeping:** <the thin slice that still delivers the core outcome, and why it's coherent alone>
**Cutting:** <item> → R<n+1> · <item> → dropped
**What the user loses:** <plainly, no spin>
**What breaks if we cut wrong:** <the dependency that makes a cut item actually mandatory>
**Decision needed from:** <who, by when>
```
