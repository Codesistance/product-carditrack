#!/usr/bin/env bash
# Runs once, after the dev container is created. The toolchain is already baked
# into the image by the Dockerfile, so this only does the workspace-dependent
# setup: restore, dev certificates, and the initial database schema.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

log() { printf '\033[0;36m[post-create]\033[0m %s\n' "$*"; }

# CardiTrack.Mobile needs the Android SDK, which is not in the default image —
# the server filter covers everything else in the solution.
log "Restoring packages"
dotnet restore CardiTrack.Server.slnf --nologo

log "Trusting the ASP.NET Core HTTPS development certificate"
dotnet dev-certs https --trust 2>/dev/null || \
  log "  (no trust store in this container — https://localhost will warn; harmless)"

# The db service is healthy before this runs (compose depends_on), so applying
# migrations here means 'dotnet run' works immediately.
log "Applying EF Core migrations to the dev database"
if dotnet ef database update \
     --project src/Infrastructure/CardiTrack.Infrastructure/CardiTrack.Infrastructure.csproj \
     --startup-project src/Presentation/CardiTrack.API/CardiTrack.API.csproj \
     --no-build 2>&1 | tail -3; then
  log "  Database ready"
else
  log "  Migration failed — run it manually once the db service is up (see .devcontainer/README.md)"
fi

cat <<'EOF'

  CardiTrack dev container ready.

    dotnet run --project src/Presentation/CardiTrack.API   # API   → http://localhost:5230
    dotnet run --project src/Presentation/CardiTrack.Web   # Web   → http://localhost:5026
    dotnet run --project src/Worker/CardiTrack.Worker      # Worker→ http://localhost:8080/healthz
    dotnet test CardiTrack.Server.slnf                     # tests (Testcontainers via the host daemon)
    terraform -chdir=infrastructure init                   # infrastructure

EOF
