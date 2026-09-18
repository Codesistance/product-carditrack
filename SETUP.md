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

The accessor grant is **per secret**, to `carditrack-deploy` — never at project
level. That binding is what keeps this workflow from reading every other secret
in the project.

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

The agent needs repo write access to `main` and nothing else — no Slack, no GCP.

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
- Grant `secretAccessor` per secret, never at project level.

## Deferred

A workflow on `issues: [labeled]` running Claude Code headless against the brief
named in the issue body, commenting the result back. The `research` label exists
for it; nothing consumes it yet. Scoped out deliberately — the chain ends at
issue creation.
