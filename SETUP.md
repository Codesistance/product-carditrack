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
| Service account | `carditrack-deploy@carditrack-490120.iam.gserviceaccount.com` |
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

## 2. Secret into Secret Manager

The secret and its IAM binding are declared in
`infrastructure/common/secret_manager.tf` (`carditrack-common-slack-bot-token`),
created with a `REPLACE_ME` placeholder that Terraform then ignores. Apply the
common stack, then load the real value:

```bash
echo -n 'xoxb-your-token' | \
  gcloud secrets versions add carditrack-common-slack-bot-token \
    --project=carditrack-490120 --data-file=-
```

The accessor grant is written **per secret**, to `carditrack-deploy`. Be clear
about what that does and does not buy: `scripts/setup-gcp-auth.sh` already grants
that same account project-level `roles/secretmanager.admin` (line 47), so it can
read every secret in the project regardless of this binding. The per-secret grant
is the correct shape and is what a dedicated identity would need, but **this
setup is not least privilege today** — see *Hardening still required* below.

## 3. Repo settings

- **Variable** `SLACK_CHANNEL` (Settings → Secrets and variables → Actions →
  Variables) = the channel **ID**, e.g. `C0XXXXXXX`. Not `#name` — the API
  accepts a name inconsistently and the failure is confusing.
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
- Grant `secretAccessor` per secret, never at project level — and note the
  project-level `secretmanager.admin` already held by `carditrack-deploy`
  currently makes that moot for this identity.

## Hardening still required

Two gaps, both found in review of this design. Neither is fixable inside the
workflow file, because both concern what the workflow file *is*.

**1. The routine can rewrite the workflow that holds the credential.** It pushes
directly to `main`, and `post-digest.yml` lives on `main`. A `push` event runs
the workflow definition *from the pushed commit*, so a prompt injection that got
the routine to commit an altered `post-digest.yml` alongside a digest would have
that altered version run — with `id-token: write` and access to Secret Manager.
No guard inside the workflow can prevent this, since the attacker's version is
the one that executes.

This is why the sandbox/runner split alone is not the whole boundary. Close it
with one of:

- **Branch protection on `main`** requiring review for `.github/workflows/**`
  (CODEOWNERS enforces this only under branch protection). The routine then
  cannot land a workflow change without a human. This is the smallest change,
  and the only cost is that the routine needs a path to commit digests that
  protection allows.
- **Scope the routine's credential** to `digests/*` via a fine-grained token or
  a GitHub App installation limited to that path.
- **Trigger from the default branch.** A `schedule:` or `workflow_run:` workflow
  always runs the definition on the default branch, not the pushed commit, so
  the routine pushing to a side branch cannot alter what runs.

**2. The posting identity is over-privileged.** `carditrack-deploy` holds
project-level `roles/secretmanager.admin`. Because this workflow assumes that
account, a successful tamper under gap 1 reaches not just the Slack token but
every secret in the project — database passwords, encryption keys, the lot. A
dedicated `carditrack-digest` service account, holding only `secretAccessor` on
`carditrack-common-slack-bot-token` and bound to the same WIF pool, would cap
the blast radius at the Slack token. That change touches the shared bootstrap
and every workflow that assumes the deploy account, so it is deliberately not in
this change.

Until both are closed, this design is still a net improvement on the routine
holding the token outright — tampering is a harder path than reading an
environment variable — but the improvement is narrower than "the routine cannot
reach the credential".

## Deferred

A workflow on `issues: [labeled]` running Claude Code headless against the brief
named in the issue body, commenting the result back. The `research` label exists
for it; nothing consumes it yet. Scoped out deliberately — the chain ends at
issue creation.
