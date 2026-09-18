#!/usr/bin/env bash
# Runs once, after the dev container is created. The toolchain is already baked
# into the image by the Dockerfile, so this only does the workspace-dependent
# setup: restore, dev certificates, and the initial database schema.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

log() { printf '\033[0;36m[post-create]\033[0m %s\n' "$*"; }

# The Android SDK cannot be fetched during the image build (it needs the Mobile
# project, and the workspace is not there yet), so an image built with
# INSTALL_MAUI=1 carries the workload and finishes the SDK here.
# Same root the toolchain script installs into and bootstrap.sh checks.
ANDROID_SDK="${ANDROID_SDK_ROOT_DIR:-${ANDROID_HOME:-$HOME/Android/Sdk}}"
if dotnet workload list 2>/dev/null | grep -q '^maui-android' && [ ! -d "$ANDROID_SDK/platforms" ]; then
  log "Installing the Android SDK into $ANDROID_SDK (maui-android workload present, SDK missing)"
  dotnet build src/Presentation/CardiTrack.Mobile/CardiTrack.Mobile.csproj -f net10.0-android \
    -t:InstallAndroidDependencies -p:AndroidSdkDirectory="$ANDROID_SDK" \
    -p:AcceptAndroidSDKLicenses=True --nologo 2>&1 | tail -3 \
    || log "  Android SDK install failed (dl.google.com unreachable?) — CardiTrack.Mobile will restore but not build"
fi

if dotnet workload list 2>/dev/null | grep -q '^maui-android' && [ -d "$ANDROID_SDK/platforms" ]; then
  log "Restoring packages (whole solution — mobile toolchain present)"
  dotnet restore CardiTrack.sln --nologo
else
  # CardiTrack.Mobile needs the Android SDK, which is not in the default image —
  # the server filter covers everything else in the solution.
  log "Restoring packages"
  dotnet restore CardiTrack.Server.slnf --nologo
fi

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
