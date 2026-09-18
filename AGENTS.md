# AGENTS.md

## Cursor Cloud specific instructions

CardiTrack is a .NET 10 solution. The standard build/run/test/migrate commands and
the full local-dev env-var set already live in the repo — don't duplicate them, read:
[`README.md`](README.md), [`.devcontainer/README.md`](.devcontainer/README.md), and
[`docs/technical/claude_cloud_environment_setup.md`](docs/technical/claude_cloud_environment_setup.md).
Day-to-day server work uses the `CardiTrack.Server.slnf` filter (everything except the
MAUI `CardiTrack.Mobile`, which needs the Android SDK).

`.cursor/environment.json` is the cloud-agent environment: its `install` runs
`INSTALL_MAUI=1 .devcontainer/bootstrap.sh` (.NET 10 SDK, `dotnet-ef`, Terraform, `psql`,
the `maui-android` workload with the Android SDK, then `dotnet restore CardiTrack.sln`), and
its `start` runs `scripts/agent/services-up.sh` (Docker daemon, Postgres, Redis). Everything
below is the non-obvious part the scripted setup does **not** cover.

### Secrets the environment needs (set in the Cursor dashboard, never in the repo)
- `GH_TOKEN` — a fine-grained PAT with **Actions: read and write** and **Contents: read**
  on `Codesistance/product-carditrack`, so `gh` can dispatch and watch workflows. Without it
  the iOS build in `scripts/agent/build-all.sh` is skipped, not faked.
- `DD_API_KEY`, `DD_APP_KEY` — for the Datadog REST API (see Datadog below). `DD_SITE` is
  not a secret and is `uk1.datadoghq.com`.

### Build everything: `scripts/agent/build-all.sh`
Server filter (Release, warning-free), then `CardiTrack.Mobile` for Android locally, then the
platforms this VM cannot build — iOS needs macOS — by dispatching **CI / Deploy Apps → Dev**
(`deploy-apps-dev.yml`) on the current branch with only the mobile ticks on, and waiting for
it. A branch dispatch runs the unsigned compile gates (Release Android, Debug iOS simulator)
and ships nothing; signed builds are `main`-only. The branch must be pushed. `--no-ci` keeps it
local; `--platform both` sends Android to CI as well. Store pushes are a separate, deliberate
step: `deploy-mobile-dev.yml`, by tag, on `main`.

### Test everything: `scripts/agent/test-all.sh`
Builds the server filter once (Release) and runs the unit and integration suites with
`TESTCONTAINERS_RYUK_DISABLED=true`. Both need a Docker daemon. It says outright that
`CardiTrack.E2ETests` is empty and `CardiTrack.Mobile` has no test project — do not report
those as green.

### Datadog
The org is on **UK1**. Two routes, pick by what the session can do:
- **MCP** (interactive login): `.cursor/mcp.json` registers Datadog's remote server at
  `https://mcp.uk1.datadoghq.com/v1/mcp`. It needs an OAuth sign-in in the Cursor UI once,
  so it works in the desktop app and not in a headless cloud agent.
- **REST** (headless): `DD_API_KEY` + `DD_APP_KEY` + `DD_SITE=uk1.datadoghq.com`, the way
  `infrastructure/datadog/README.md` and `.claude/skills/carditrack-trace-triage` query it
  (`curl -H "DD-API-KEY: $DD_API_KEY" -H "DD-APPLICATION-KEY: $DD_APP_KEY" https://api.$DD_SITE/api/v2/...`).
  Egress needed: `api.uk1.datadoghq.com`. The application key in use cannot query metrics
  (403); logs, spans, monitors and CI visibility work.

### Services (all `dotnet run` from their project dir; see README for exact ports)
- `src/Presentation/CardiTrack.API` — REST API (`http://localhost:5230`, Swagger at `/swagger`). Core.
- `src/Worker/CardiTrack.Worker` — cron background jobs (baselines, alerts, sync, retention). Core. `/healthz` on `PORT` (default 8080).
- `src/Presentation/CardiTrack.Web` — Blazor dashboard. **Currently broken** (see below).

### Postgres + Redis need a running Docker daemon
The API/Worker need Postgres 17 + Redis 7, and the unit + integration test suites start
Postgres via Testcontainers — all require a Docker daemon. This VM's base image ships
**no Docker**. When `docker` is absent, install Docker CE, then note that on this kernel
Docker 29 must use `fuse-overlayfs` with the containerd snapshotter disabled and
`iptables`/`ip6tables` switched to the legacy backend, or the daemon won't start. Write
`/etc/docker/daemon.json` as `{"storage-driver":"fuse-overlayfs","features":{"containerd-snapshotter":false}}`,
`update-alternatives --set iptables /usr/sbin/iptables-legacy` (and `ip6tables`), start
`dockerd`, then `docker compose up -d db redis`. To use the socket without sudo:
`sudo usermod -aG docker "$USER"` and open a new terminal (never `chmod 666` the socket — that
hands every process on the VM root through Docker). `scripts/agent/services-up.sh` does the
install, the daemon configuration and the group membership when Docker is absent. Run tests
with `TESTCONTAINERS_RYUK_DISABLED=true` (matches CI).

Apply EF migrations once the db is up (command in README). MedGemma/Ollama and the AI
pipeline are optional (`docker compose --profile full ...`) and not needed for the core stack.

### Extra env vars required to boot the API/Worker locally
Beyond the documented dev env vars, a bare `dotnet run` of the API fails on startup
without these — they are only injected by Terraform in Cloud Run, so they're absent from
`appsettings*.json` and `docker-compose.yml`. They are non-secret config; any non-empty
value works locally:
- `Pipeline__Audience`, `Pipeline__ServiceAccount` — gate the internal pipeline-enqueue endpoint (API startup).
- `Health__Token` — the `/health` endpoint is gated behind an `X-Health-Token` header carrying this value. Note `/health` is liveness-only (the DB/Redis checks are commented out), so a `200` proves the process is up, not that the DB is reachable — the integration tests are the real DB proof.

### Known pre-existing issues (NOT environment problems — do not "fix" as setup)
- `CardiTrack.Web` fails at startup: `INotificationGapResolver` isn't registered for `UserService` in its `Program.cs` (that dependency is only wired by `AddPushServices`, which Web doesn't call). The Web shell can't run standalone until that DI gap is fixed.
- One failing unit test: `HealthInsightServiceStatusTests.TellsTheModelToStayNonClinicalAndBrief` asserts the prompt contains `"under 12 words"`, but the source now says `"under 15 words"` — test/source drift, unrelated to setup.
- `NotificationDispatchWorker` and other push paths call `GetRequired("GCP_PROJECT_ID")`; without GCP config those phases log an error every tick locally. Expected and non-fatal — the rest of the Worker runs fine.

### Lint / test
The enforced lint gate is a **warning-free build** (analyzers); CI runs no `dotnet format`,
and `dotnet format --verify-no-changes` reports pre-existing whitespace/charset diffs that
are not enforced. Tests: `dotnet test CardiTrack.Server.slnf` (unit + integration; the
`CardiTrack.E2ETests` project currently contains no tests).

### dotnet-ef PATH
`dotnet-ef` installs to `~/.dotnet/tools`; if it's not found, `export PATH="$PATH:$HOME/.dotnet/tools"`.
