# GitHub repository access — public for Actions minutes, one human writer

CardiTrack's source lives in `Codesistance/product-carditrack`. It stays
**public** so standard GitHub-hosted runners are free. Write is limited to one
named human. Machine identities required to operate CI/CD stay; no other people.

## Why it is public

GitHub Actions standard runners are **free on public repositories**. Private
repositories draw from the plan quota — **2,000 minutes/month** on GitHub Free
for organizations, with macOS billed at 10×. That quota is exhausted.

Making this repository private would put every `main` push and PR back on that
meter (a full apps-dev run is still ~185 billed minutes after #327) and would
stop CI until the next billing cycle or a paid plan. Public visibility is the
Actions-minutes control, not an invitation to collaborate.

## What GitHub will not do

Public means anyone can clone, browse, and fork. Collaborator lists only govern
**write**. There is no GitHub setting that keeps a repository public (and
therefore on free Actions) and also hides it from everyone except one account.

If the source must be unread by the internet, the repository has to be private
and CI has to move off the Free-plan quota (paid GitHub, or wait for the monthly
reset). Those two goals are mutually exclusive on GitHub-hosted runners.

## Write policy

| Role | GitHub login |
|---|---|
| Sole human admin | `@marigbede` |

Do not add collaborators, teams, or outside collaborators. Invite a person only
when this policy is explicitly changed.

Same-repo PRs (this owner, Cursor cloud agents, GitHub Apps already installed)
can write. Fork PRs from other accounts do not run Copilot review.

## CI triggers

**Every deploy workflow is `workflow_dispatch` only.** Nothing builds, tests or
deploys on a push to `main` or on a pull request. Cloud agents already build each
change as it is written, so an automatic run on top of that was paying twice for
the same answer.

This used to be a flag — `.github/ACTIONS_ON_PUSH`, with `push` and
`pull_request` triggers behind it — held at `0` for long enough that the triggers
and their reader job were only ever adding a skipped job to every run. All three
are gone. The triggers themselves are the switch now, so re-enabling push CI
means adding a trigger back, not flipping a value.

One rule outlives the flag: **never give the root job of a chain an `if:`.** A
skipped job skips everything beneath it, past any `always()` in between. When the
old flag-reader skipped on a dispatch, `env` survived via `always()` and
succeeded, but the job after it — which has no `if:` of its own — was skipped
anyway, taking every build, deploy and Terraform job with it. Those runs reported
success having done nothing (2026-08-15, run 31904971393). `env` is the root now
and carries no condition. Each of the three workflows also keeps a
`dispatch-sanity` job that fails the run if the chain collapses that way again.

Copilot review is requested on non-draft, same-repo PRs when they open, reopen or
leave draft (short ubuntu job) — not on every push, since each review bills about
five Actions minutes; re-requests are deliberate, one per triage round. Merging is
manual: there is no auto-merge workflow (removed 2026-08-21 — with a single
maintainer who reads every review anyway, its label-plus-gates machinery and
its quarter-hourly sweep bought nothing that `gh pr merge` does not).

| Workflow | When it runs |
|---|---|
| CI / Deploy Apps → Dev | **workflow_dispatch only** |
| Deploy Mobile → Dev | **workflow_dispatch only**, on `main` (`platform` = android / ios / both, `tag`); pushes a tag's existing builds, builds nothing |
| Deploy Infrastructure → Dev / Common | **workflow_dispatch only** |
| Deploy Apps / Infra → Prod | **workflow_dispatch only** (unchanged) |
| Request Copilot review | `pull_request` opened / reopened / ready_for_review (not synchronize) |

Merging deploys nothing — not Dev Cloud Run, and never TestFlight / Play. Dispatch
**CI / Deploy Apps → Dev** on `main` with the lanes you want; the mobile ones are per
platform, and iOS is off by default because it is the expensive one. Then push the tag
it created to the stores with **Deploy Mobile → Dev**.

## Operator steps (console)

1. **Keep visibility Public.** Do not flip it to private while the Free-plan
   Actions quota is exhausted.
2. **Settings → Collaborators and teams.** Only `@marigbede`. Remove team grants.
3. **Settings → Actions → General → Fork pull request workflows** from
   first-time contributors: require approval (Copilot-review workflow already
   skips forks).
4. **Do not** enable **Require review from Code Owners** as a required check.
   `.github/CODEOWNERS` is `* @marigbede`; the author's approval does not
   count, so that setting would block every PR this owner opens. Use a ruleset
   to require a pull request before merging if you want `main` protected, with
   an admin bypass for this owner.

## What this repository enforces in code

| Control | Where |
|---|---|
| Default code owner is `@marigbede` | [`.github/CODEOWNERS`](../../.github/CODEOWNERS) |
| Deploy workflows carry no `push` / `pull_request` trigger | [`.github/workflows/`](../../.github/workflows/) |
| Fork PRs do not get Copilot review requested | [`.github/workflows/request-copilot-review.yml`](../../.github/workflows/request-copilot-review.yml) |

## Machine identities that must keep access

These are not people. Revoking them breaks deploy, review, or cloud agents:

- GitHub Actions (`GITHUB_TOKEN`, and `AUTOMERGE_TOKEN` — still required: it is
  the PAT the Copilot-review workflow needs, since `GITHUB_TOKEN` cannot add
  that reviewer. The name outlived the auto-merge workflow it was created for.)
- GitHub Copilot pull-request reviewer
- Cursor GitHub App (cloud agents, PR automation)

Do not add a second human to unblock any of those.
