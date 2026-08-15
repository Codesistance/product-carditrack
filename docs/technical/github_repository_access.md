# GitHub repository access — one human reader/writer

CardiTrack's source, architecture, and operator runbooks live in
`Codesistance/product-carditrack`. That tree is not wearer health data, but it
is the control plane for a health-data product (Terraform, CI, OAuth inventory,
DPIA). Access is **one named human**. Machine identities required to operate
CI/CD stay; no other people.

## Why "public + one reader" is impossible

GitHub **public** visibility means anyone on the internet can clone, browse,
and fork. Collaborator lists only govern **write**. There is no GitHub setting
that keeps a repository public and also hides it from everyone except one
account.

The equivalent of "only one person can read or write" is:

1. **Visibility = private**
2. **Exactly one human collaborator** with admin (`@marigbede`)
3. **No extra teams, outside collaborators, or user grants**

Organization owners of `Codesistance` can still see a private org repository.
If that org has more than one owner, those owners are additional human readers.
True single-human access requires either a single org owner or transferring the
repository to that person's personal account as private.

## Current owner

| Role | GitHub login |
|---|---|
| Sole human admin | `@marigbede` |

Do not add collaborators, teams, or outside collaborators. Invite a person only
when this policy is explicitly changed.

## Operator steps (console — cannot be done from this repo)

These are GitHub administration actions. They are not in Terraform, and CI
tokens in this repository cannot change visibility.

1. **Settings → General → Danger Zone → Change repository visibility → Private.**
   Confirm. Forking of a private org repo is then controlled under
   **Settings → General → Features → Allow forking**; leave it off.
2. **Settings → Collaborators and teams.** Remove every human except
   `@marigbede`. Remove every team grant.
3. **Settings → Rules → Rulesets** (or classic branch protection) on `main`:
   - Require a pull request before merging
   - Require review from Code Owners (`.github/CODEOWNERS` is `* @marigbede`)
4. Confirm **Codesistance** has no extra organization owners who should not
   read this repository.

Making a previously public repository private does **not** recall copies that
were already cloned, forked, or archived while it was public. Treat that history
as published; rotate anything that was a live secret.

## What this repository enforces in code

| Control | Where |
|---|---|
| Default code owner is `@marigbede` | [`.github/CODEOWNERS`](../../.github/CODEOWNERS) |
| CI fails while visibility is public | [`.github/workflows/repo-access-gate.yml`](../../.github/workflows/repo-access-gate.yml) |

The Actions gate reads `github.event.repository.private` from the event payload
(no extra token). It will stay red on every PR and on `main` until step 1
above is done.

## Machine identities that must keep access

These are not people. Revoking them breaks deploy, review, or cloud agents:

- GitHub Actions (`GITHUB_TOKEN` and `AUTOMERGE_TOKEN` for Copilot review / auto-merge)
- GitHub Copilot pull-request reviewer
- Cursor GitHub App (cloud agents, PR automation)

Do not add a second human to unblock any of those.

## Clone

Private clone requires GitHub authentication:

```bash
git clone https://github.com/Codesistance/product-carditrack.git
```
