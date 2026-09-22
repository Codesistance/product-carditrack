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

The one fact that shapes everything else: **CI does not gate PRs here.** Every
deploy workflow is dispatch-only, so the only automation a PR triggers is the
Copilot-review request — no build, no tests, no migration check runs against your
diff
([github_repository_access.md](../../../docs/technical/github_repository_access.md)).
Local verification is the entire gate. Nothing downstream catches what you skip.

## 1. Verify — you are the CI

Run what CI would have run, before pushing:

| Gate | Command | Notes |
|---|---|---|
| Warning-free build | `dotnet build CardiTrack.Server.slnf` | The enforced lint gate is **zero warnings**. Nothing mechanical enforces it — no `TreatWarningsAsErrors`, and CI runs no `dotnet format` — so you are the enforcement. Do not run `dotnet format --verify-no-changes`; it flags pre-existing whitespace/charset diffs that are not enforced. |
| Tests | `TESTCONTAINERS_RYUK_DISABLED=true dotnet test CardiTrack.Server.slnf` | Unit + integration suites start Postgres via Testcontainers, so Docker must be up (cloud sessions start it in the SessionStart hook). `CardiTrack.E2ETests` contains no tests. CI builds and tests in `-c Release`; add it if analyzers behave differently. |
| EF model drift | `dotnet ef migrations has-pending-model-changes --project src/Infrastructure/CardiTrack.Infrastructure/CardiTrack.Infrastructure.csproj --startup-project src/Presentation/CardiTrack.API/CardiTrack.API.csproj` with `SKIP_DB_CONTEXT_VALIDATION=true` | Run it whenever the diff touches entities or EF configuration. This is the classic silent miss: the check exists only in the dispatch-gated workflow, so a PR with un-migrated model changes looks clean until the next deploy fails. |

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

**The `tools/` blind spot.** `tools/AiSplitEvaluator`, `tools/ChatRoutingEval` and
`tools/HealthApiProbe` are in **neither** `CardiTrack.sln` nor
`CardiTrack.Server.slnf`. Nothing in section 1 compiles them, so a break there
survives a clean, warning-free build and surfaces only when someone runs the tool.

The sharp edge is dependency injection. Each tool builds its own service
provider, so it is a **separate composition root**: add a constructor dependency
to a shared type and the tool's registrations go stale silently — a build proves
nothing about DI, which resolves at runtime. Copilot caught exactly this on
#1178, where `AiSplitEvaluator` constructs its own `UnitOfWork` and needed
`IGenerationLeaseRepository` registered. `TestDatabaseFixture` is a composition
root too, but that one fails loudly in the test run.

So: when a diff changes a shared constructor or adds a repository/service
interface, grep `tools/` for the affected type and build the tools you touched
(`dotnet build tools/<Tool>`) even though CI never will.

## 2. Push and open the PR

Work happens on a feature branch — **never on `main`** (write access is one human
plus the installed apps; merges are deliberate). Use the session's designated
branch when it has one; otherwise any descriptive name off `main` is fine
(`claude/*` and `feat/*` are both in use). The branch name is not the rule — not
touching `main` is.

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
requests Copilot on `opened`, `ready_for_review` and `reopened` for a non-draft
same-repo PR — **not on `synchronize`**. So the *first* review arrives on its own
and **every later round you request by hand.** That is the normal path, not a
failure signal. Expecting a push to trigger a re-review is how a triage round
stalls: no request is made, no review is coming, and the wait looks like slowness
rather than a missing step.

Not-on-push is deliberate. Each review costs ~5 Linux Actions minutes billed to
this repo, and with a review per push September 2026 ran to 355 reviews across 90
branches — one branch took 18 — most of them on intermediate pushes nobody read.
Hence one push per round, one review per round.

**The mechanics live in
[`.cursor/rules/pr-copilot-review.mdc`](../../../.cursor/rules/pr-copilot-review.mdc)** —
treat that as the source of truth and read it before re-requesting, rather than
trusting a restatement here that can drift out of sync with the workflow. As it
stands, re-request through the GraphQL `requestReviews` mutation:

```
gh api graphql -f query='mutation{requestReviews(input:{
  pullRequestId:"<PR node id>",botIds:["BOT_kgDOCnlnWA"],union:true}){
  pullRequest{reviewRequests(first:5){nodes{requestedReviewer{
  __typename ... on Bot{login}}}}}}}'
```

- `BOT_kgDOCnlnWA` is `copilot-pull-request-reviewer`. It is **not**
  `copilot-swe-agent` — that id is accepted silently and registers no review.
- `union:true` keeps the reviewers already on the PR instead of replacing them.
- **Read the mutation's own response** to confirm the bot is listed. A 200 alone
  does not mean Copilot was added.

The workflow's own `gh pr edit --add-reviewer copilot` is not a session
fallback: it works *there* because the workflow runs as the `AUTOMERGE_TOKEN`
PAT. The Cursor GitHub App token gets 403 on it, and cloud sessions have no
`gh pr` at all. In a cloud session the `request_copilot_review` MCP tool is the
route to try — and it gets the same treatment, confirm the reviewer actually
registered before you start waiting.

"Finished" is observable: a review by `copilot-pull-request-reviewer[bot]`
(state `COMMENTED`) whose `commit_id` is the PR's current head SHA. It typically
lands within a few minutes of the request. Check `gh pr view` /
`pull_request_read get_reviews` after a couple of minutes rather than
sleep-polling; in cloud sessions, `subscribe_pr_activity` delivers the review as
an event.

The `commit_id` check matters most on re-review rounds: a stale review from the
previous head looks exactly like a fresh one in a comment list. Nothing is
"finished" until a review's `commit_id` matches the head you just pushed. No
review after ~10 minutes → on the *first* review check the workflow run; on a
*re-request* check that the request registered at all (the usual cause), not the
code.

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
gates, push **once**, then re-request the review by hand (section 3). Per-comment
pushes do not each earn a review — the workflow does not run on push — they just
leave the PR sitting at an unreviewed head while you wait for something nobody
asked for.

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

**`main` moves under you.** Triage rounds take hours, and `main` does not wait —
PR #1178 ran eight rounds while `main` gained five commits. Before calling a PR
merge-ready, merge `origin/main` in again and re-run the section 1 gates against
the result; the gates you ran in round one were against a base that no longer
exists.

Then re-check the **prose** the merge did not conflict on. Git merges docs
textually, so two edits that never touch the same line merge clean and still
contradict each other: on #1178 a clean merge left `docs/release_matrix.md`
confidently describing a Journal-tab card that #1177 had just deleted. No
compiler and no test catches that. Re-read the docs your diff touches as they now
stand on the merged result, not as you wrote them.

**Health data.** PR threads are public (the repo is public on purpose). Never
quote a wearer value from test data, logs or traces into a comment — name the
field, redact the value. Same rule as the sibling triage skills.

## 5. Merge-ready, then stop

Merge-ready means, on the final head commit — with `origin/main` merged in
recently enough to mean something: warning-free build, tests green, Copilot round
converged, every thread answered and addressed threads resolved. Report that
state.

- **Merging is manual and the maintainer's call.** There is no auto-merge (the
  workflow was removed 2026-08-21). Do not merge unless the user asks; when they
  do, squash — `main`'s history is one commit per PR.
- **Merging does not deploy.** No workflow runs on a push to `main`, so nothing
  rolls out on merge. If the change should reach dev, dispatch **CI / Deploy Apps → Dev**
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
- **The Copilot request mechanics — source of truth:** [.cursor/rules/pr-copilot-review.mdc](../../../.cursor/rules/pr-copilot-review.mdc) · the workflow that implements them: [.github/workflows/request-copilot-review.yml](../../../.github/workflows/request-copilot-review.yml)
