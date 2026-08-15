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

`.github/ACTIONS_ON_PUSH` is the flag (`0` / `false` / `1` / `true`).
Repo variable `ACTIONS_ON_PUSH` overrides the file when set.

| Value | Push to `main` and pull_request | workflow_dispatch |
|---|---|---|
| `0` or `false` (current) | Skip expensive jobs (flag reader only) | Runs |
| `1` or `true` | Full apps-dev / infra CI | Runs |

Copilot review and auto-merge always run on the PR; they are short ubuntu jobs.

| Workflow | When it runs |
|---|---|
| CI / Deploy Apps → Dev | `ACTIONS_ON_PUSH` plus **workflow_dispatch** |
| Deploy Infrastructure → Dev / Common | `ACTIONS_ON_PUSH` plus **workflow_dispatch** |
| Deploy Apps / Infra → Prod | **workflow_dispatch only** (unchanged) |
| Request Copilot review | `pull_request` opened / synchronize / reopened / ready_for_review |
| Auto-merge | label, review, schedule, `workflow_run`, `workflow_dispatch` |

With the flag at `0`, Dev Cloud Run / TestFlight / Play do not start on merge.
Dispatch **CI / Deploy Apps → Dev** on `main`, or set the flag to `1`.

## Operator steps (console)

1. **Keep visibility Public.** Do not flip it to private while the Free-plan
   Actions quota is exhausted.
2. **Settings → Collaborators and teams.** Only `@marigbede`. Remove team grants.
3. **Settings → Actions → General → Fork pull request workflows** from
   first-time contributors: require approval (Copilot-review workflow already
   skips forks).
4. **Do not** enable **Require review from Code Owners** as a required check.
   `.github/CODEOWNERS` is `* @marigbede`; the author's approval does not
   count, so that setting would block every PR this owner opens. Auto-merge
   already waits on Copilot. Use a ruleset to require a pull request before
   merging if you want `main` protected, with an admin bypass for this owner.

## What this repository enforces in code

| Control | Where |
|---|---|
| Default code owner is `@marigbede` | [`.github/CODEOWNERS`](../../.github/CODEOWNERS) |
| Push/PR CI gated by `0`/`1` flag | [`.github/ACTIONS_ON_PUSH`](../../.github/ACTIONS_ON_PUSH) |
| Fork PRs do not get Copilot review requested | [`.github/workflows/request-copilot-review.yml`](../../.github/workflows/request-copilot-review.yml) |

## Machine identities that must keep access

These are not people. Revoking them breaks deploy, review, or cloud agents:

- GitHub Actions (`GITHUB_TOKEN` and `AUTOMERGE_TOKEN` for Copilot review / auto-merge)
- GitHub Copilot pull-request reviewer
- Cursor GitHub App (cloud agents, PR automation)

Do not add a second human to unblock any of those.
