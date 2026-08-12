# Claude Code Cloud Environment Setup (Operator)

Config for the **New cloud environment** dialog in Claude Code (name, network access,
environment variables, setup script) so a fresh cloud session can build, run, and test
`CardiTrack.Server.slnf` (API, Web, Worker) with no manual follow-up.

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
| `dl.google.com` | Android SDK for `CardiTrack.Mobile` | Only if `INSTALL_MAUI=1` and building `CardiTrack.sln` instead of the server filter |
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
AI__Public__Model=gemini-2.0-flash
AI__Public__ApiKey=unset-local-placeholder
AI__Public__TimeoutSeconds=60
AI__Private__BaseUrl=http://localhost:11434
AI__Private__Model=hf.co/unsloth/medgemma-1.5-4b-it-GGUF:Q4_K_M
AI__Private__TimeoutSeconds=120
DOTNET_CLI_TELEMETRY_OPTOUT=1
DOTNET_NOLOGO=1
```

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
# Only brings up Postgres/Redis for local dev. The .NET/Terraform toolchain still
# installs via the .claude/settings.json SessionStart hook
# (.devcontainer/bootstrap.sh), which runs after Claude Code launches and the repo
# exists — this script must not fail the session, so no `set -e`.
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
  exit 0
fi

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

log "Postgres + Redis up. Once Claude Code launches, apply migrations:"
log "  cd src/Infrastructure/CardiTrack.Infrastructure && dotnet ef database update --startup-project ../../Presentation/CardiTrack.API"
exit 0
```

- The toolchain (.NET 10 SDK, `dotnet-ef`, Terraform, PostgreSQL client) is *not*
  installed here — that's the `.claude/settings.json` `SessionStart` hook's job
  (`.devcontainer/bootstrap.sh`), which runs once the repo actually exists.
- Container names (`carditrack-db`, `carditrack-redis`) make the script idempotent
  across re-runs on a warm container — `docker start` on an existing container,
  `docker run` only the first time.
- EF migrations are deliberately left for after Claude Code launches, once
  `dotnet ef` (installed by the hook) and the repo are both present — run the command
  the script logs at the end, or ask Claude Code to run it.
- Build/test with `dotnet build CardiTrack.Server.slnf` / `dotnet test
  CardiTrack.Server.slnf`. That solution filter excludes `CardiTrack.Mobile` (MAUI),
  which needs the Android SDK from `dl.google.com` — the one real blocker left if you
  build `CardiTrack.sln` directly instead.
- MedGemma/Ollama is not started by this script (multi-GB pull, only needed for AI
  insight debugging): `docker compose --profile full up ollama medgemma-init` once the
  repo is checked out.

## References

- [.devcontainer/README.md](../../.devcontainer/README.md) — the dev container this
  config mirrors, including the SessionStart hook and network-policy caveats table
- [.devcontainer/bootstrap.sh](../../.devcontainer/bootstrap.sh) — toolchain
  provisioning this setup script calls
- [README.md](../../README.md) — general local-development setup
