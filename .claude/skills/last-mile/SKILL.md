---
name: last-mile
description: Drive a finished CardiTrack change through the last mile — verify it locally (warning-free build, tests, pending-migration check), push, open a ready-for-review PR, wait for the Copilot review, triage its comments to convergence, and end merge-ready. Use when a change is done and needs to ship — "open a PR", "get this reviewed", "handle the Copilot comments", "is this merge-ready?" — or after any skill or task that ends with code ready to go out.
---

# CardiTrack last mile

[CLAUDE.md](../../../CLAUDE.md)'s Code Quality section states the contract in two
lines: changes are verified before they are applied, and PRs open ready for review,
get a Copilot review, and have its comments triaged rather than applied blindly.
This skill is that contract as a procedure, with the repo mechanics that make it
work.

The one fact that shapes everything else: **CI does not gate PRs here.**
[`.github/ACTIONS_ON_PUSH`](../../../.github/ACTIONS_ON_PUSH) is `0`, so the only
automation a PR triggers is the Copilot-review request — no build, no tests, no
migration check runs against your diff
([github_repository_access.md](../../../docs/technical/github_repository_access.md)).
Local verification is the entire gate. Nothing downstream catches what you skip.

## 1. Verify — you are the CI

Run what CI would have run, before pushing:

| Gate | Command | Notes |
|---|---|---|
| Warning-free build | `dotnet build CardiTrack.Server.slnf -c Release` | The enforced lint gate is **zero warnings**. Nothing mechanical enforces it — no `TreatWarningsAsErrors`, and CI runs no `dotnet format` — so you are the enforcement. Release matches what the dispatch-gated CI builds. Do not run `dotnet format --verify-no-changes`; it flags pre-existing whitespace/charset diffs that are not enforced. |
| Tests | `TESTCONTAINERS_RYUK_DISABLED=true dotnet test CardiTrack.Server.slnf -c Release` | Unit + integration suites start Postgres via Testcontainers, so Docker must be up (cloud sessions start it in the SessionStart hook). `CardiTrack.E2ETests` contains no tests. |
| EF model drift | `SKIP_DB_CONTEXT_VALIDATION=true dotnet ef migrations has-pending-model-changes --project src/Infrastructure/CardiTrack.Infrastructure/CardiTrack.Infrastructure.csproj --startup-project src/Presentation/CardiTrack.API/CardiTrack.API.csproj --configuration Release --no-build` | Run it whenever the diff touches entities or EF configuration (`--no-build` reuses the Release build from the first gate). This is the classic silent miss: the check exists only in the dispatch-gated workflow, so a PR with un-migrated model changes looks clean until the next deploy fails. |

Then read your own diff adversarially — `git diff origin/main` — asking what a
reviewer would reject: scope creep beyond the ask, a violated binding rule
(Worker job exclusivity, the GCP AI-pipeline exception, full-bleed page shells —
all in [CLAUDE.md](../../../CLAUDE.md)), a comment that talks to the reviewer
instead of the next reader.

**The MAUI blind spot.** `CardiTrack.Mobile` is outside the solution filter and
cannot build without the Android SDK, so mobile XAML/page changes ship
build-unverified from a server environment. Testable mobile logic lives in
`CardiTrack.Mobile.Core`, which *is* covered — keep logic there. When a change
touches unbuildable mobile code, say so in the PR body instead of implying it was
verified.

## 2. Push and open the PR

Work happens on the session's designated `claude/*` branch — never on `main`
(write access is one human plus the installed apps; merges are deliberate).

```
git push -u origin <branch>
gh pr create --title "<headline>" --body-file <file>
```

(Cloud sessions have no `gh`; use the GitHub MCP tools — `create_pull_request`,
`pull_request_read` — for every step written as `gh` here.)

- **Never draft** ([CLAUDE.md](../../../CLAUDE.md)) — and mechanically, the
  Copilot-request workflow skips draft PRs, so a draft gets no review at all.
- **The PR title is the future `main` commit.** This repo squash-merges; `main`
  is one commit per PR, titled `<PR title> (#N)`. Write it in house style — an
  imperative, human sentence about the outcome, not the mechanism: "Tell families
  all is well when a member has been quiet for a week", not "Add
  QuietMemberDigestJob".
- There is no PR template. The body says what changed and why, what verification
  actually ran, and what could not be verified (the MAUI blind spot above).

## 3. Wait for the Copilot review — it is part of the definition of done

[`request-copilot-review.yml`](../../../.github/workflows/request-copilot-review.yml)
requests Copilot automatically on every open, reopen, ready-for-review and push
of a non-draft same-repo PR. Do not request it by hand unless the request never
appears — that means the workflow failed (it needs the `AUTOMERGE_TOKEN` PAT;
the default token cannot add Copilot as a reviewer), and the fallback is
`gh pr edit <n> --add-reviewer copilot` or the `request_copilot_review` MCP tool.

"Finished" is observable: a review by `copilot-pull-request-reviewer[bot]`
(state `COMMENTED`) whose `commit_id` is the PR's current head SHA. It typically
lands within a few minutes of the request. Check `gh pr view` /
`pull_request_read get_reviews` after a couple of minutes rather than
sleep-polling; in cloud sessions, `subscribe_pr_activity` delivers the review as
an event. No review after ~10 minutes → check the workflow run, not the code.

**Read the review body, not just the inline threads.** Copilot buries real
findings in two body sections that never become threads: *"Suppressed comments
(N)"*, and on re-reviews *"Previously missed — in code that hasn't changed since
the last review"*. This repo treats those as first-class findings (PR #471 fixed
a suppressed one alongside the inline comment). Triage all three sources.

## 4. Triage — fix or answer, never blind-apply

Each Copilot finding is a bug report to verify against the code, not an
instruction. Read the code it points at, then decide:

- **Real** → fix it. Keep the fix minimal — the finding's scope, not a refactor.
- **Wrong, or right-but-out-of-scope** → leave the code alone and say why on the
  thread. A stated reason is the deliverable; silent disagreement looks like an
  oversight.

Batch the round: verify and fix everything from one review, re-run the section 1
gates, and push **once** — every push triggers a fresh Copilot re-review, so
per-comment pushes multiply review rounds for nothing.

After pushing, close the loop on each thread the way this repo does: reply
"Fixed in `<short-sha>`" (or the reason for declining), append the Claude Code
attribution footer, and resolve the threads you addressed. Suppressed and
previously-missed findings have no thread — cover them in the same reply or the
PR conversation.

**Convergence.** The round is done when a re-review on the final head says
*"generated no new comments"* — a "previously missed" finding in unchanged code
still counts as new and gets triaged. If rounds stop converging — each fix draws
a new or reshaped finding — stop pushing for the bot and raise what is still
flagged once, with your assessment, to the user.

**Health data.** PR threads are public (the repo is public on purpose). Never
quote a wearer value from test data, logs or traces into a comment — name the
field, redact the value. Same rule as the sibling triage skills.

## 5. Merge-ready, then stop

Merge-ready means, on the final head commit: warning-free build, tests green,
Copilot round converged, every thread answered and addressed threads resolved.
Report that state.

- **Merging is manual and the maintainer's call.** There is no auto-merge (the
  workflow was removed 2026-08-21). Do not merge unless the user asks; when they
  do, squash — `main`'s history is one commit per PR.
- **Merging does not deploy.** With `ACTIONS_ON_PUSH` at `0`, nothing rolls out
  on merge. If the change should reach dev, dispatch **CI / Deploy Apps → Dev**
  on `main` — and note that run also executes the build, test and migration
  checks this PR skipped, so a skipped section 1 gate surfaces there, at the
  worst possible time.

## Boundaries

**This skill ships a change that is already decided and written.** Whether to do
it is [issue-triage](../issue-triage/SKILL.md); where the code belongs is
[software-architect](../software-architect/SKILL.md). It does not deploy, does
not touch `main` directly, and does not close the PR conversation on the
maintainer's behalf.

## Reference map

- The contract: [CLAUDE.md](../../../CLAUDE.md) · build/test environment detail: [AGENTS.md](../../../AGENTS.md)
- CI gating and write policy: [docs/technical/github_repository_access.md](../../../docs/technical/github_repository_access.md)
- What CI would run (dispatch-only): [.github/workflows/deploy-apps-dev.yml](../../../.github/workflows/deploy-apps-dev.yml)
- The Copilot request mechanics: [.github/workflows/request-copilot-review.yml](../../../.github/workflows/request-copilot-review.yml)
