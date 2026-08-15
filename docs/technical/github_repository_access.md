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
still run CI. Fork PRs from other accounts do not — the `env` job `if` on the
apps-dev and infra workflows.

## Operator steps (console)

1. **Keep visibility Public.** Do not flip it to private while the Free-plan
   Actions quota is exhausted.
2. **Settings → Collaborators and teams.** Only `@marigbede`. Remove team grants.
3. **Settings → Actions → General → Fork pull request workflows** from
   first-time contributors: require approval (defence in depth; the workflow
   `if` already skips forks).
4. **Settings → Rules → Rulesets** (or classic branch protection) on `main`:
   require a pull request, and require review from Code Owners
   (`.github/CODEOWNERS` is `* @marigbede`).

## What this repository enforces in code

| Control | Where |
|---|---|
| Default code owner is `@marigbede` | [`.github/CODEOWNERS`](../../.github/CODEOWNERS) |
| Fork PRs do not run apps-dev or infra CI | `deploy-apps-dev.yml`, `deploy-infra-dev.yml`, `deploy-infra-common.yml` |

## Machine identities that must keep access

These are not people. Revoking them breaks deploy, review, or cloud agents:

- GitHub Actions (`GITHUB_TOKEN` and `AUTOMERGE_TOKEN` for Copilot review / auto-merge)
- GitHub Copilot pull-request reviewer
- Cursor GitHub App (cloud agents, PR automation)

Do not add a second human to unblock any of those.
