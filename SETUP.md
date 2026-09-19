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
| Service account | `carditrack-deploy@carditrack-490120.iam.gserviceaccount.com` (deploys) |
| Digest identity | `carditrack-digest@carditrack-490120.iam.gserviceaccount.com` (this workflow; no project roles) |
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

**Rerun the bootstrap first.** `carditrack-digest` is created by
`scripts/setup-gcp-auth.sh`, not by Terraform, and this project has already run
an older copy of that script — which skips anything that exists and so will not
add the account on its own:

```bash
bash scripts/setup-gcp-auth.sh
```

That run also adds `attribute.job_workflow_ref` to the OIDC provider and binds
the account to the posting workflow. Until it has happened, the Terraform
binding below refers to a principal that does not exist and the workflow cannot
authenticate.

The secret and its IAM binding are declared in
`infrastructure/common/secret_manager.tf` (`carditrack-common-slack-bot-token`),
created with a `REPLACE_ME` placeholder that Terraform then ignores. Then apply
the common stack and load the real value:

```bash
echo -n 'xoxb-your-token' | \
  gcloud secrets versions add carditrack-common-slack-bot-token \
    --project=carditrack-490120 --data-file=-
```

The accessor grant is **per secret**, to `carditrack-digest` — an identity with
no project-level roles, so this binding is the whole of its read access, and the
bootstrap now fails closed if that account is ever found holding one. It is
deliberately not `carditrack-deploy`, which holds project-level
`roles/secretmanager.admin` and could read every secret regardless of any
per-secret binding. See *Hardening still required* for what this does and does
not protect against.

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
- Grant `secretAccessor` per secret, never at project level — and grant it to
  `carditrack-digest`, not `carditrack-deploy`: a per-secret grant to an account
  that already holds project-level `secretmanager.admin` isolates nothing.

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

**2. The posting identity is over-privileged.** Closed. Posting now assumes
`carditrack-digest`, created by `scripts/setup-gcp-auth.sh` with **no
project-level roles**; its only grant is `secretAccessor` on
`carditrack-common-slack-bot-token`. A legitimate run of this workflow can reach
the Slack token and nothing else — previously it assumed `carditrack-deploy`,
which holds project-level `roles/secretmanager.admin` and can read every secret
in the project.

`carditrack-digest` is also bound to the posting workflow rather than to the
repository, via `attribute.workflow_ref`, so no other workflow authenticating on
its own can assume it — every deploy workflow already requests `id-token: write`,
so a repository-wide binding would have made "scoped identity" untrue. The
bootstrap asserts that binding is the only one on the account, across every role
rather than just `workloadIdentityUser`, and fails naming anything else.

One more limit, stated because the obvious reading of the above is wrong:
`carditrack-deploy` holds **project-level** `serviceAccountTokenCreator` and
`serviceAccountUser`, granted by `scripts/setup-gcp-auth.sh` because the deploy
workflows need them. Project-level roles reach every service account in the
project, so anything holding the deploy account can mint a token for
`carditrack-digest`. It gains nothing by doing so — the deploy account already
holds `secretmanager.admin` — but the accurate claim is about *reach*, not
*access*: the digest identity cannot be used to read more than the Slack token,
and assuming it requires either being the posting workflow or already holding a
strictly more powerful account. The bootstrap prints these holders on every run
so the claim cannot quietly drift again.

The first real run is what proves the claim matches: if the binding is wrong,
`post-digest.yml` fails at the auth step and posts nothing, which is the
direction you want it to fail in. Check the run before assuming the digest is
live.

Be clear about the limit, because it is easy to overrate: this does **not**
defend against gap 1. `carditrack-deploy` is still bound repository-wide, so a
tampered workflow can simply ask for that account instead and read everything.
Narrowing the deploy account the same way would touch every deploy workflow and
is not attempted here. What the scoped identity buys is a correct blast radius
for the workflow as written, and a real reduction once gap 1 is closed. Gap 1
remains the load-bearing fix; this is defence in depth behind it.

## Deferred

A workflow on `issues: [labeled]` running Claude Code headless against the brief
named in the issue body, commenting the result back. The `research` label exists
for it; nothing consumes it yet. Scoped out deliberately — the chain ends at
issue creation.
