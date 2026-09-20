# Digest routine — setup

The chain:

```
routine → digests/YYYY-MM-DD.json → push to main
       → .github/workflows/post-digest.yml → Slack (parent + threads)
       → button → prefilled GitHub issue → stop
```

The routine ingests untrusted web content, so it holds **no** Slack credential.
It has repo write access and nothing else. Posting happens on a GitHub Actions
runner. Do not collapse these back together.

## Already done — do not redo

CardiTrack's Workload Identity Federation was set up for the deploy workflows and
is reused here as-is:

| | |
|---|---|
| Project | `carditrack-490120` (number `206164751924`) |
| Pool / provider | `carditrack-pool` / `github` |
| Service account | `carditrack-deploy@carditrack-490120.iam.gserviceaccount.com` (deploys; also posts the digest — no separate identity) |
| Attribute condition | `assertion.repository=='Codesistance/product-carditrack'` |

That condition is the thing that stops any repo on GitHub assuming the service
account. It is already set — see `scripts/setup-gcp-auth.sh`, which is idempotent
and skips anything that exists. `.github/workflows/_env.yml` is the single source
of truth for these values, and `post-digest.yml` reads them from it, so there are
no project IDs to fill in anywhere.

## 1. Slack app

1. Create the app, add the **`chat:write`** bot scope, install to the workspace.
2. Copy the bot token (`xoxb-…`).
3. **Invite the bot to the channel** — `/invite @CardiTrack digest`. Without
   this the post fails with `not_in_channel`, which `post-digest.sh` surfaces
   verbatim.

## 2. Secrets into Secret Manager

`slack-bot-token` and `slack-channel-id` are both declared in
`infrastructure/common/secret_manager.tf`, inside `store_distribution_secrets`
— the same set and the same `carditrack-deploy` accessor grant every other
common secret in that file uses, not a bespoke resource block each. Both are
created with a `REPLACE_ME` placeholder that Terraform then ignores. The
channel ID is a GCP secret rather than a GitHub Actions repo variable
on purpose: it still has to be set by hand either way, and this keeps every
`post-digest.yml` input — token and channel alike — on the one loading path.
Apply the common stack, then load the real values:

```bash
echo -n 'xoxb-your-token' | \
  gcloud secrets versions add carditrack-common-slack-bot-token \
    --project=carditrack-490120 --data-file=-

echo -n 'C0XXXXXXX' | \
  gcloud secrets versions add carditrack-common-slack-channel-id \
    --project=carditrack-490120 --data-file=-
```

The channel value is the **ID**, e.g. `C0XXXXXXX` — not `#name`. Slack's API
accepts a name inconsistently and the failure is confusing.

The accessor grant goes to `carditrack-deploy` — the same account every deploy
workflow already authenticates as, and the one `post-digest.yml` now uses too.
It already holds project-level `roles/secretmanager.admin`, so this per-secret
binding adds nothing it didn't already have; it's there only for consistency
with the rest of the file, not for isolation. See *Accepted tradeoff* below for
what that costs.

## 3. Repo settings

- **Label** `research`, which the issue button applies. Create it before the
  first post; GitHub silently drops a label that does not exist, and the issue
  opens unlabelled.

## 4. Claude Code Cloud

Create the environment and agent against this repo, then register the cron
deployment at **07:00 Europe/London** running `/digest`.

The agent is given repo write access and no credential of its own — no Slack, no
GCP. Note that "no GCP" holds only while it cannot alter the workflow that does
have GCP access; see *Hardening still required*.

## 5. Smoke test

Hand-commit a digest with one FYI item and push to `main`:

```bash
cat > digests/$(date +%F).json <<'JSON'
{
  "date": "REPLACE_WITH_TODAY",
  "summary": "*CardiTrack digest — smoke test* · 1 item · 1 FYI",
  "items": [
    {
      "slug": "smoke-test",
      "title": "Smoke test — ignore",
      "url": "https://example.com/smoke-test",
      "severity": "FYI",
      "category": "dependencies",
      "brief": "research/queue/smoke-test.md",
      "text": "*FYI* — Smoke test, ignore.\nhttps://example.com/smoke-test"
    }
  ]
}
JSON
```

Confirm: parent message posts, one threaded reply appears, the **Open issue**
button opens a prefilled issue with the `research` label. Then delete the file
and the test issue — but leave the digest file's *history* alone, since the
workflow only posts files a push adds, so a later delete cannot re-trigger it.

## Gotchas

- `SLACK_CHANNEL` must be the ID (`C0…`), not `#name`.
- Slack returns HTTP 200 with `ok:false` on failure. `post-digest.sh` checks the
  payload, and falls back to a readable message when the body is not JSON at all
  (a gateway error arrives as HTML). Do not reduce it to a status-code check.
- Button URLs must be URL-encoded. `post-digest.sh` does this with jq's `@uri`;
  the routine must not hand-build the URL.
- The workflow diffs the **whole pushed range** (`github.event.before..sha`), not
  `HEAD^ HEAD`, so a push carrying several commits posts every digest in it. It
  filters to added files only, so editing a digest after the fact does not
  re-post it.

## Hardening still required

**1. The routine can rewrite the workflow that holds the credential.** Still
open, and it is the one that matters. The routine pushes directly to `main`, and
`post-digest.yml` lives on `main`. A `push` event runs the workflow definition
*from the pushed commit*, so a prompt injection that got the routine to commit an
altered `post-digest.yml` alongside a digest would have that altered version run,
with `id-token: write`. No guard inside the workflow can prevent this, since the
attacker's version is the one that executes.

Close it with one of:

- **A repository ruleset restricting `.github/workflows/**`** so the routine
  cannot land a workflow change without review, while still committing digests.
  Smallest change, keeps this design intact, and it is what the digest identity
  below assumes.
- **Scope the routine's credential** to `digests/*` via a fine-grained token or
  a GitHub App installation limited to that path.
- **Trigger from the default branch.** A `schedule:` or `workflow_run:` workflow
  runs the definition on the default branch rather than the pushed commit, so a
  routine pushing to a side branch — with no `main` write at all — cannot alter
  what runs.

**2. The posting identity is `carditrack-deploy` — project-level
`secretmanager.admin`, not scoped to this one secret.** Accepted, not closed.

An earlier revision ran posting as a separate `carditrack-digest` account:
created by `scripts/setup-gcp-auth.sh` with no project-level roles at all, its
only grant `secretAccessor` on `carditrack-common-slack-bot-token`, and bound
to the posting workflow specifically via `attribute.workflow_ref` rather than
to the repository — a real narrowing over `carditrack-deploy`, which is bound
repository-wide and holds project-level `secretmanager.admin`.

It was reverted on 2026-09-20. That second bootstrapped identity lived outside
Terraform, and `setup-gcp-auth.sh` skips anything that already exists — so when
`carditrack-digest` was added to the script after the pool and `carditrack-deploy`
were already bootstrapped, a rerun on this project silently skipped creating
it. The account never existed in GCP. Posting failed for at least three runs
running up to 2026-09-20 (a 404 on the secret, then "Gaia id not found" for the
account itself) before anyone noticed the digest had never reached Slack. One
fewer moving part — reusing `carditrack-deploy`, the account every deploy
workflow already authenticates as and that this project actually keeps
provisioned — was judged worth the wider grant.

What this costs: a legitimate run of `post-digest.yml` can now read every
secret in the project, not just the Slack token. What it does not change:
`carditrack-deploy` was already bound repository-wide and already the account
gap 1 describes a tampered workflow reaching for, so this does not widen gap 1
— there is just no narrower fallback identity behind it anymore. Re-introduce
a scoped `carditrack-digest`-style identity (and fix the bootstrap script's
skip-if-exists check so an account added to the script after the fact actually
gets created) if that blast-radius reduction is worth the operational cost
again.

## Deferred

A workflow on `issues: [labeled]` running Claude Code headless against the brief
named in the issue body, commenting the result back. The `research` label exists
for it; nothing consumes it yet. Scoped out deliberately — the chain ends at
issue creation.
