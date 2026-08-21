---
name: issue-triage
description: Triage a CardiTrack GitHub issue through four expert lenses — product-manager (is it worth doing, which wave), software-architect (where the fix lives, which binding rule constrains it), cloud-architect (only when GCP or infrastructure is in play), and security-architect (only when auth, secrets, data exposure, or the attack surface is in play) — into one verdict, priority and first step, posted back on the issue. Use when asked to triage an issue, groom the backlog, decide "should we do #N", or when a new issue lands and needs a call.
---

# CardiTrack issue triage

Turn an issue into a decision: **is this real, is it worth doing now, where does the fix
live, and what would go wrong if someone fixed it the obvious way.**

Four skills already hold that judgement — [`product-manager`](../product-manager/SKILL.md),
[`software-architect`](../software-architect/SKILL.md), [`cloud-architect`](../cloud-architect/SKILL.md),
[`security-architect`](../security-architect/SKILL.md).
This skill does not duplicate them. It decides **which lenses apply**, asks each a fixed
short question set so the answers compose, and resolves the disagreements between them.

Triage ends at a verdict and a first step. It does not write the fix.

## 1. Intake

```
gh issue view <n> --json number,title,body,state,labels,author,url,createdAt,comments
```

Accepts an issue number, a URL, or pasted text. **Read the comments, not just the body** —
issue bodies in this repo are often one line and the real requirement arrives later.

**Attachments are the common blocker.** `gh` returns the image markdown, not the image.
Issue #67 is a screenshot with the title "Misc fixes"; #7 is two sentences plus a
screenshot. You cannot triage a screenshot you have not seen — ask the user to paste or
describe it. Do not reconstruct intent from a title.

## 2. Four cheap gates, before you spend a lens

In order. Any hit ends triage early — say which gate stopped it.

| Gate | Check | If hit |
|---|---|---|
| **Triageable** | Is there enough here to act on? | `NEEDS INFO` — list the specific questions, don't guess |
| **Duplicate** | `gh issue list --state all --search "<keywords>"` — open *and* recently closed | `DUPLICATE` — name the issue it duplicates |
| **Already shipped** | Build status in [release_matrix.md](../../../docs/release_matrix.md), then the code | `STALE` — cite the file or the matrix row |
| **One issue or several** | Bundles are frequent here ("Misc fixes") | `SPLIT` — triage each item separately, recommend splitting |

## 3. Choose the lenses

| Lens | Run it when | Skip it when |
|---|---|---|
| `product-manager` | **Always.** Every issue has a "is this worth doing, and when" answer. | Never. |
| `software-architect` | The issue implies a change under `src/` or `tests/`. | Pure ops or process issues with no code surface — #39 (Google verification), #40 (BAA). Say you skipped it. |
| `cloud-architect` | See triggers below. | Everything else. |
| `security-architect` | See triggers below. | Everything else. |

**`cloud-architect` triggers** — any one is enough: `infrastructure/**` or Terraform;
Cloud Run, Cloud SQL, Pub/Sub, GCS, Secret Manager; the GCP AI pipeline in
[llm_design.md](../../../docs/llm_design.md); IAM, service accounts, or the OAuth project
layout; GCP cost; load balancer, Cloud Armor, or edge; the deploy workflows in
[.github/workflows/](../../../.github/workflows/).

**`security-architect` triggers** — any one is enough: authentication or authorization
(Auth0, JWT, `[Authorize]`, roles, the caregiver/CardiMember access split); secrets,
tokens, keys, or OAuth flows; encryption at rest or in transit; health data or PII being
stored, logged, displayed, or sent somewhere new; CORS, security headers, cookies, or
webhook/callback validation; injection surface (SQL, command, log); IAM bindings,
firewall rules, or public exposure in Terraform; a reported vulnerability or CVE.

IAM and Terraform trigger both `cloud-architect` and `security-architect` — run both:
cloud answers *which service and what it costs*, security answers *who can attack it and
what they get*. When only the attack surface is in question, security alone is enough.

Load each lens with the Skill tool, one at a time, and only when it applies. **Do not run a
lens for completeness** — a lens with nothing to say produces filler that buries the lenses
that do. Name what you skipped in one line so the reader knows it was a choice.

## 4. What to ask each lens

Fixed question sets. Answers stay short — the synthesis is the deliverable, not the lens
transcripts.

### product-manager

1. Real user pain or a preference — and whose? The caregiver is the buyer; the CardiMember
   often has no login and no say.
2. Bug or enhancement? A bug is a gap against a **written** spec — cite the spec line, or
   state that no spec covers it (which makes it an enhancement, however broken it feels).
3. Which of the 4 Big Risks actually move. Full table only if the issue proposes new scope.
4. Wave (R1–R4) and plan gate, per [release_matrix.md](../../../docs/release_matrix.md) —
   the canonical sequencing doc.
5. Any standing viability constraint hit — 100-wearer cap, Fitbit shutdown September 2026,
   no billing before R2, per-metric consent, the missing mobile health-data disclosure.
6. RICE **only** if this competes with named backlog items. Skip it for plain bugs.

→ **Verdict:** Do now / Do in wave R_ / Needs discovery / Kill.

### software-architect

1. Which project and folder owns the fix, per the placement table.
2. Which binding rule constrains it — Worker exclusivity, the GCP AI-pipeline exception,
   the zero-package core, full-bleed page shells. **"No binding rule in play" is a valid
   and useful answer.**
3. Blast radius: one file / one project / crosses a layer boundary / needs a new project.
4. **Would the obvious fix create a violation?** This is the highest-value line in the whole
   triage — the fix a reporter describes is often the one that puts a job in the API or a
   package in `Domain`.
5. Effort tier: trivial / small / needs design.

→ **Verdict:** Safe to fix as described / Fix, but not the way it is asked / Needs an ADR.

### cloud-architect

1. Which GCP surface, and which environment — remember prod has no LB or Cloud Armor, so
   edge findings land in dev only.
2. Security and reliability weight: this is health data.
3. Cost delta, if any.
4. Terraform-first? Infra changes go through `infrastructure/environments/*.tfvars`, never
   the console.

→ **Verdict:** Proceed / Proceed with guardrails / Blocked on provisioning.

### security-architect

1. Attack surface and STRIDE category, in one line — who is the attacker, what do they
   get? Wearer health data is the prize; a finding that exposes it outranks everything.
2. Exploitable **today**, or latent? Dev-only surfaces, endpoints behind auth that is not
   yet wired, and flows blocked on provisioning are still findings — but rate them for
   what an attacker can reach now.
3. Already an accepted risk? Check the skill's accepted-risks table (DP key ring on GCS,
   prod edge not enabled, and the rest) — cite the decision and stop; do not re-flag it.
4. **Would the obvious fix weaken a control?** Widened CORS, a logged token, a swallowed
   authorization failure, an over-broad IAM grant to unblock a deploy — name the trap.
5. Severity — Critical / High / Medium / Low per the skill's table. Critical maps to
   triage P0.

→ **Verdict:** No security surface / Fix at the stated severity / Escalate to P0.

## 5. Synthesize — the lenses are inputs, this is the output

```
| Lens | Verdict | The one thing that matters |
|---|---|---|
| Product   | ... | ... |
| Architect | ... | ... |
| Cloud     | ... | (or: not run — no GCP surface) |
| Security  | ... | (or: not run — no security surface) |
```

Then the call:

- **Verdict** — `ACCEPT` / `NEEDS INFO` / `DUPLICATE` / `WONT FIX` / `STALE` / `SPLIT`
- **Priority** — P0–P3, per the table below, with the reason
- **Wave** — R1–R4, or "unscheduled" (which is an answer; "soon" is not)
- **Owning surface** — API / Web / Mobile / Worker / GCP / Docs / Ops
- **First step** — the one concrete action, naming the file or the person

| Priority | Meaning on this product |
|---|---|
| **P0** | Health data exposed, a missed or wrong alert, data loss, or auth broken. Ahead of feature work. |
| **P1** | Blocks a wave gate or a dated external deadline — Fitbit September 2026, restricted-scope verification, the mobile disclosure that gates submission. |
| **P2** | Real user pain with a workaround. |
| **P3** | Cosmetic, or a nice-to-have with no wave. |

### When lenses disagree, print the disagreement

Do not average two verdicts into a mushy middle. The conflict is usually the finding:

- **Product says R1, architect says it needs a new project** → either the wave is wrong or
  the design is. Say which, and why.
- **Product says small, architect says it crosses a layer boundary** → the effort estimate
  in the issue is wrong; re-scope before it is picked up.
- **Architect says trivial, cloud says Terraform plus a deploy** → it is not trivial.
- **Product says kill, architect says the code is already half there** → sunk cost is not a
  reason; product wins on scope, architecture wins on how.
- **Security says Critical, product says P3 cosmetic** → security wins; health-data
  exposure is P0 regardless of how small the issue looks. The reverse — security says
  Low, product says urgent — does not downgrade the product verdict.
- **Security flags it, but it is on the accepted-risks list** → the finding is `STALE` as
  a security issue; cite the recorded decision instead of re-raising it.

## 6. Report, then post

Show the user the triage first. Post only on confirmation.

```
gh issue comment <n> --body-file <file>
```

Comment shape — keep it short enough that a maintainer reads all of it:

```markdown
## Triage

**<VERDICT>** · <priority> · <wave> · <owning surface>

<One paragraph: what this actually is, and why the verdict.>

| Lens | Verdict | Note |
|---|---|---|
| Product | ... | ... |
| Architecture | ... | ... |
| Cloud | ... | ... |
| Security | ... | ... |

**First step:** <concrete action, with file path or owner>
**Watch out for:** <the trap the architect lens found, if any>
```

**Labels — use only what exists.** The repo has the GitHub defaults: `bug`,
`documentation`, `duplicate`, `enhancement`, `good first issue`, `help wanted`,
`invalid`, `question`, `wontfix`. (An `automerge` label may still exist on the repo; it
does nothing since the auto-merge workflow was removed on 2026-08-21 — do not apply it.) There is **no priority, wave, or surface label, no
milestone, and no project board** — so priority and wave live in the comment text. Do not
invent a label mid-triage; if the gap keeps hurting, propose the whole label set once as
its own change.

```
gh issue edit <n> --add-label bug
```

**Do not close issues.** `WONT FIX`, `DUPLICATE` and `STALE` are recommendations with
reasons attached. Closing is the maintainer's call.

## Worked shape

Issue #7 — *emergency contact phone placeholder defaults to +1 US instead of +44 UK*:

- **Gates:** triageable (screenshot plus a clear description); not a duplicate; not shipped.
- **Lenses:** product ✓, architect ✓, cloud ✗ (no GCP surface — a MAUI placeholder
  string), security ✗ (a display default; the phone number is not stored, logged, or sent
  anywhere new).
- **Product:** real pain, wrong user assumption for a UK-first launch; a bug only if the
  screen spec states a locale default — check it, and if it does not, this is a small
  enhancement plus a spec gap worth recording.
- **Architect:** `CardiTrack.Mobile` UI; no binding rule in play; one file. The trap is
  hardcoding `+44` in place of `+1` — that is the same bug with a different constant.
- **Call:** `ACCEPT` · P2 · R1 · Mobile. First step: derive the default from device region
  in the account-setup screen, and note the missing locale rule in the screen spec.

## Boundaries

**This skill decides.** It does not fix, does not branch, does not open a PR. When the
verdict is `ACCEPT` and the user wants it built, that is a new task — fresh worktree, own
PR, per the repo's workflow.

**Health data.** Issue bodies, comments and screenshots can carry real wearer data. Triage
with the full detail locally, but never quote a value into a triage comment — name the
field and redact the value.

## Reference map

- Lenses: [product-manager](../product-manager/SKILL.md) · [software-architect](../software-architect/SKILL.md) · [cloud-architect](../cloud-architect/SKILL.md) · [security-architect](../security-architect/SKILL.md)
- Binding rules: [CLAUDE.md](../../../CLAUDE.md)
- Sequencing and build status: [docs/release_matrix.md](../../../docs/release_matrix.md)
- Doc precedence when sources conflict: [docs/readme.md](../../../docs/readme.md)
- Sibling triage skills: `pr-comment-triage` (review feedback) · [carditrack-trace-triage](../carditrack-trace-triage/SKILL.md) (a trace or a 500)
