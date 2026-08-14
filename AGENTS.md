# AGENTS.md

## Cursor Cloud specific instructions

CardiTrack is a .NET 10 solution. The standard build/run/test/migrate commands and
the full local-dev env-var set already live in the repo — don't duplicate them, read:
[`README.md`](README.md), [`.devcontainer/README.md`](.devcontainer/README.md), and
[`docs/technical/claude_cloud_environment_setup.md`](docs/technical/claude_cloud_environment_setup.md).
Day-to-day server work uses the `CardiTrack.Server.slnf` filter (everything except the
MAUI `CardiTrack.Mobile`, which needs the Android SDK).

The update script runs `.devcontainer/install-toolchain.sh` (.NET 10 SDK, `dotnet-ef`,
Terraform, `psql`) then `dotnet restore CardiTrack.Server.slnf`. Everything below is the
non-obvious part the scripted setup does **not** cover.

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
`sudo chmod 666 /var/run/docker.sock`. Run tests with `TESTCONTAINERS_RYUK_DISABLED=true` (matches CI).

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
