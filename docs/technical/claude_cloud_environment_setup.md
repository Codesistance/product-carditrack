# Claude Code Cloud Environment Setup (Operator)

Config for the **New cloud environment** dialog in Claude Code (name, network access,
environment variables, setup script) so a fresh cloud session can build, run, and test
`CardiTrack.Server.slnf` (14 projects — API, Web, Worker, the two Pipeline hosts
(HealthWebhookReceiver, PipelineJobs), Mobile.Core, and the test projects; everything
except `CardiTrack.Mobile`) with no manual follow-up.

This is a different surface from `.devcontainer/`: the dev container is for VS Code /
`devcontainer up`, and its `SessionStart` hook (`.claude/settings.json` →
`.devcontainer/bootstrap.sh`) already provisions the toolchain for Claude Code cloud
sessions *after* Claude Code launches. The dialog's **Setup script** field runs
*before* Claude Code launches, and the cloud environment has no dev-container compose
network — so it needs its own script to bring up Postgres/Redis, not just the
toolchain. `bootstrap.sh` is idempotent, so re-running it via the hook right after
costs a few seconds and is expected, not a bug.

## Name

`CardiTrack Server Dev` — cosmetic only.

## Network access

Pick the broadest tier the dialog offers, not the "Trusted" default — Docker Hub and
`registry.terraform.io` are commonly excluded from restricted-network presets and both
are required below. If the dialog exposes a custom domain allowlist instead of (or in
addition to) a tier, use the list below.

### Core (required — build/run/test API, Web, Worker)

```
github.com
raw.githubusercontent.com
codeload.github.com
api.github.com
archive.ubuntu.com
security.ubuntu.com
dot.net
api.nuget.org
nuget.org
registry-1.docker.io
auth.docker.io
production.cloudfront.docker.com
index.docker.io
```

| Hosts | Used for |
| --- | --- |
| GitHub hosts | Clone + restore |
| Ubuntu archive hosts | `apt-get install dotnet-sdk-10.0`, `postgresql-client`, base utils (`install-toolchain.sh`) |
| `dot.net` | Fallback `dotnet-install.sh` if the apt install fails |
| NuGet hosts | `dotnet restore` |
| Docker Hub hosts | Pulling `postgres:17-alpine` / `redis:7-alpine`; Testcontainers pulling `postgres` for the integration/unit test suite |

### Optional — only if that part of the stack is touched

| Hosts | Needed for | Notes |
| --- | --- | --- |
| `releases.hashicorp.com`, `registry.terraform.io` | `infrastructure/` (Terraform) | `registry.terraform.io` is the one the dev container README already flags as commonly blocked |
| `packages.cloud.google.com` | gcloud CLI | Only if `INSTALL_GCLOUD=1`; off by default on the cloud bootstrap path |
| `dl.google.com` | Android SDK for `CardiTrack.Mobile` | With `INSTALL_MAUI=1` (recommended below), so Android compiles in the session |
| `api.uk1.datadoghq.com` | Datadog REST API (logs, spans, monitors) | With `DD_API_KEY` / `DD_APP_KEY` set — see [Datadog](#datadog) |
| `api.github.com` (already in Core) | `gh workflow run` / `gh run watch` | With `GH_TOKEN` set — see [Building and testing everything](#building-and-testing-everything) |
| `generativelanguage.googleapis.com` | Live Gemini calls | Only if the placeholder `AI__Public__ApiKey` is swapped for a real key |
| `registry.ollama.ai`, `huggingface.co`, `hf.co` | Pulling the MedGemma model into local Ollama | Only for AI-insight debugging (`docker compose --profile full up ollama medgemma-init`) |

## Environment variables

These mirror the throwaway local-dev values already committed in the repo's own
`docker-compose.yml` and `.devcontainer/docker-compose.yml` (both explicitly marked
"local-development key ONLY") — not real secrets, safe to paste into the dialog
despite its "don't add secrets or credentials" warning.

```
ASPNETCORE_ENVIRONMENT=Development
ConnectionStrings__DefaultConnection=Host=localhost;Port=5432;Database=carditrack;Username=postgres;Password=postgres
ConnectionStrings__Redis=localhost:6379
Encryption__Key=W2iqrS4VgOXDgwZQWCGj716pKcu2nLs1tk5j66oNzBY=
AI__Public__Kind=Gemini
AI__Public__Model=gemini-3.5-flash
AI__Public__ApiKey=unset-local-placeholder
AI__Public__TimeoutSeconds=60
AI__Private__BaseUrl=http://localhost:11434
AI__Private__Model=hf.co/unsloth/medgemma-1.5-4b-it-GGUF:Q4_K_M
AI__Private__TimeoutSeconds=120
DOTNET_CLI_TELEMETRY_OPTOUT=1
DOTNET_NOLOGO=1
INSTALL_MAUI=1
DD_SITE=uk1.datadoghq.com
```

`INSTALL_MAUI=1` makes the `SessionStart` hook (`.devcontainer/bootstrap.sh`) install the
`maui-android` workload, a JDK and the Android SDK, and restore `CardiTrack.sln` rather than
the server filter, so `CardiTrack.Mobile` compiles for Android inside the session. It needs
`dl.google.com` in network access. (Before 2026-09-17 the variable was documented but
`install-toolchain.sh` never acted on it.)

### Secrets (the environment's secrets store, not the variables box)

| Secret | Purpose | Scope |
| --- | --- | --- |
| `GH_TOKEN` | `gh workflow run` / `gh run watch` — the iOS build, which only CI can do | Fine-grained PAT on `Codesistance/product-carditrack`: **Actions: read and write**, **Contents: read**, **Pull requests: read and write** (the latter for `gh pr` work) |
| `DD_API_KEY`, `DD_APP_KEY` | Datadog REST API | A Datadog API key plus an application key scoped to logs, APM and monitors read |

`AI__Public__ApiKey` stays a placeholder by design — chat/report calls get a 401,
everything else (including MedGemma-backed insights against local Ollama) still runs.
Put a real Gemini key through the platform's actual secrets mechanism if live calls are
needed, not this box.

## Setup script

The dialog runs this **before Claude Code launches and before the repo is checked
out** — confirmed by hitting `bash: /home/user/.devcontainer/bootstrap.sh: No such
file or directory` (exit 127) when an earlier version of this script assumed the repo
was already on disk. A failed setup script blocks the session from starting at all, so
this version depends on nothing inside the repo and never hard-fails:

```bash
#!/bin/bash
# Runs before the repo is checked out, so it cannot reference anything inside it.
# Brings up Postgres/Redis for local dev, and pre-installs the JDK MAUI Android
# tooling needs (a small, repo-independent apt package) so the heavier
# `INSTALL_MAUI=1 ./.devcontainer/install-toolchain.sh` step — run once the repo
# exists, since it needs the repo's install-toolchain.sh plus a multi-GB workload
# + Android SDK pull — has one less thing to do. The .NET/Terraform toolchain
# itself still installs via the .claude/settings.json SessionStart hook
# (.devcontainer/bootstrap.sh), which runs after Claude Code launches — this
# script must not fail the session, so no `set -e`.
set +e

log() { printf '[setup] %s\n' "$*"; }

# Start the Docker daemon if the binary is present but nothing is running yet.
if command -v dockerd >/dev/null 2>&1 && ! docker info >/dev/null 2>&1; then
  log "starting dockerd"
  if [ "$(id -u)" -eq 0 ]; then
    nohup dockerd >/var/log/dockerd.log 2>&1 &
  else
    sudo -n true 2>/dev/null && sudo -b nohup dockerd >/var/log/dockerd.log 2>&1
  fi
  for i in $(seq 1 15); do docker info >/dev/null 2>&1 && break; sleep 1; done
fi

if ! command -v docker >/dev/null 2>&1 || ! docker info >/dev/null 2>&1; then
  log "no usable docker daemon — skipping Postgres/Redis bring-up; run 'docker compose up -d db redis' by hand once the repo is checked out"
else
  # Same images/credentials as the repo's docker-compose.yml, started standalone
  # since the compose file itself isn't checked out yet at this point.
  docker start carditrack-db carditrack-redis >/dev/null 2>&1
  docker inspect carditrack-db >/dev/null 2>&1 || docker run -d --name carditrack-db \
    -e POSTGRES_DB=carditrack -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD=postgres \
    -p 5432:5432 postgres:17-alpine
  docker inspect carditrack-redis >/dev/null 2>&1 || docker run -d --name carditrack-redis \
    -p 6379:6379 redis:7-alpine

  for i in $(seq 1 30); do
    docker exec carditrack-db pg_isready -U postgres -d carditrack >/dev/null 2>&1 && break
    sleep 2
  done
  log "Postgres + Redis up."
fi

# Lightweight, repo-independent MAUI prerequisite (~200MB, not the multi-GB
# workload/SDK). Safe to install unconditionally; skipped if already present.
if ! java -version >/dev/null 2>&1; then
  log "installing OpenJDK 21 (MAUI Android prerequisite)"
  if [ "$(id -u)" -eq 0 ]; then
    apt-get update -qq && apt-get install -y -qq openjdk-21-jdk-headless
  else
    sudo -n apt-get update -qq && sudo -n apt-get install -y -qq openjdk-21-jdk-headless
  fi
fi

log "Once Claude Code launches:"
log "  Migrations: cd src/Infrastructure/CardiTrack.Infrastructure && dotnet ef database update --startup-project ../../Presentation/CardiTrack.API"
log "  Mobile (MAUI, needs dl.google.com in network access): INSTALL_MAUI=1 ./.devcontainer/install-toolchain.sh"
exit 0
```

- The .NET/Terraform/`dotnet-ef`/PostgreSQL-client toolchain is *not* installed
  here — that's the `.claude/settings.json` `SessionStart` hook's job
  (`.devcontainer/bootstrap.sh`), which runs once the repo actually exists.
- Container names (`carditrack-db`, `carditrack-redis`) make the script idempotent
  across re-runs on a warm container — `docker start` on an existing container,
  `docker run` only the first time.
- The Docker-unavailable branch no longer early-`exit`s — it used to, which also
  skipped the JDK install and closing log lines below it; now it just skips the
  DB/Redis bring-up and falls through.
- EF migrations are deliberately left for after Claude Code launches, once
  `dotnet ef` (installed by the hook) and the repo are both present — run the command
  the script logs at the end, or ask Claude Code to run it.
- OpenJDK 21 installs unconditionally because it's cheap and repo-independent — it's
  a real MAUI Android prerequisite either way, so pre-installing it shaves time off
  the `INSTALL_MAUI=1` step without paying for the actual multi-GB workload/SDK pull
  on every session. See [Mobile (MAUI) coverage](#mobile-maui-coverage) below.
- Build/test with `dotnet build CardiTrack.Server.slnf` / `dotnet test
  CardiTrack.Server.slnf`. That solution filter excludes `CardiTrack.Mobile` (MAUI).
- MedGemma/Ollama is not started by this script (multi-GB pull, only needed for AI
  insight debugging): `docker compose --profile full up ollama medgemma-init` once the
  repo is checked out.

## Building and testing everything

Two scripts are the agent's entry points, in a Claude Code cloud session and in a
Cursor cloud agent alike (Cursor's `.cursor/environment.json` runs the same bootstrap):

```bash
scripts/agent/build-all.sh   # server filter → Android locally → iOS (and Android if skipped) via CI
scripts/agent/test-all.sh    # unit + integration, Release, Testcontainers; names what has no tests
```

iOS cannot be linked on Linux, so "all builds" means the Android compile happens here and
the iOS (device, signed) build happens by dispatching **CI / Deploy Apps → Dev** on the
current branch with only the mobile ticks on and waiting for it — a branch dispatch builds
and ships nothing. That needs
`GH_TOKEN` (above) and the branch pushed; without them the script reports the step as
skipped rather than passed. `scripts/agent/services-up.sh` starts the Docker daemon and
Postgres/Redis for the tests and for running the API locally.

## Datadog

Two routes to the CardiTrack org, which is on **UK1**:

- **MCP** — `.mcp.json` at the repo root registers Datadog's remote MCP server
  (`https://mcp.uk1.datadoghq.com/v1/mcp`, project scope). It needs an interactive OAuth
  sign-in the first time, so it is for the desktop app and the VS Code / Cursor extensions
  (`.vscode/mcp.json` and `.cursor/mcp.json` carry the same entry), not for a headless
  cloud session.
- **REST** — `DD_API_KEY` + `DD_APP_KEY` from the secrets store and `DD_SITE` from the
  variables (also set for every session by `.claude/settings.json`). The user-level
  `datadog-pup` skill and the repo's `carditrack-trace-triage` skill query
  `https://api.uk1.datadoghq.com` with them; `infrastructure/datadog/README.md` has the
  curl shape. Add `api.uk1.datadoghq.com` to network access. The application key in use
  cannot query metrics (403); logs, spans, monitors and CI visibility work.

## Mobile (MAUI) coverage

With `INSTALL_MAUI=1` in the environment variables (the recommended setting above), the
session compiles `CardiTrack.Mobile` for Android. Without it the setup matches
`.devcontainer/install-toolchain.sh`'s `INSTALL_MAUI=0` default and stays scoped to
`CardiTrack.Server.slnf`: the `maui-android` workload and Android SDK add several GB and
need `dl.google.com`, which some restricted network policies exclude.

**Where Mobile actually gets built:** the mobile lanes of
`.github/workflows/deploy-apps-dev.yml`, one tick per platform (`mobile_android`,
`mobile_ios`, `mobile_windows`), each on its own GitHub-hosted runner — not through
this environment, not through `CardiTrack.sln`. The runner images ship an Android SDK,
so it needs no `dl.google.com` access; it just runs `dotnet workload install
maui-android` and builds. Those jobs are the authoritative check for Mobile — treat a
local/cloud MAUI build as faster local feedback, never as a substitute for them. From
a session with a `GH_TOKEN` that has the `workflow` scope:

```bash
gh workflow run deploy-apps-dev.yml --ref <branch> -f api=false -f web=false -f worker=false -f pipeline=false -f webhook=false -f mobile_android=true -f mobile_ios=true
gh run watch --exit-status "$(gh run list --workflow deploy-apps-dev.yml --branch <branch> --limit 1 --json databaseId --jq '.[0].databaseId')"
```

A dispatch on a branch builds and ships nothing (every deploy, image push and archive
job is guarded on `main`); on `main` the same ticks also archive the builds to GCS and
tag, and `deploy-mobile-dev.yml` pushes that tag to the stores. On dispatch the ticks,
not the paths filter, decide what builds, so a change anywhere in the app's dependency
graph (`CardiTrack.Mobile`, `CardiTrack.Mobile.Core`, `CardiTrack.Domain`,
`CardiTrack.Application`) is covered by dispatching it.

**In a session that was bootstrapped without `INSTALL_MAUI=1`**, the same layer can be
added afterwards:
1. Add `dl.google.com` to the environment's network access (see the Optional table
   above).
2. From the session (the pre-checkout setup script cannot, the repo is not there yet):
   ```bash
   INSTALL_MAUI=1 ./.devcontainer/install-toolchain.sh && dotnet restore CardiTrack.sln
   ```
3. Build with `scripts/agent/build-all.sh --no-ci`, or target the project directly:
   `dotnet build src/Presentation/CardiTrack.Mobile/CardiTrack.Mobile.csproj -f net10.0-android`.

`install-toolchain.sh` degrades gracefully if `dl.google.com` is still blocked: the
workload installs and C#/XAML compile far enough to surface language-level warnings,
even though the final APK/AOT/R8 steps need the full SDK.

## References

- [.devcontainer/README.md](../../.devcontainer/README.md) — the dev container this
  config mirrors, including the SessionStart hook and network-policy caveats table
- [.devcontainer/bootstrap.sh](../../.devcontainer/bootstrap.sh) — toolchain
  provisioning this setup script calls
- [README.md](../../README.md) — general local-development setup
