#!/usr/bin/env bash
# Session bootstrap for containers whose base image we don't control — Claude
# Code cloud sessions, Codespaces on a generic image, ad-hoc CI runners.
#
# Brings such a container up to the same toolchain the dev container image bakes
# in (see Dockerfile), then warms the NuGet cache so the first build is not cold.
#
# Safe to run repeatedly: every step short-circuits once satisfied. Wired as a
# SessionStart hook in .claude/settings.json.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MARKER="${HOME}/.cache/carditrack-restore-complete"

log() { printf '\033[0;36m[bootstrap]\033[0m %s\n' "$*"; }

# ── Toolchain ────────────────────────────────────────────────────────────────
"${REPO_ROOT}/.devcontainer/install-toolchain.sh"

# ── PATH for .NET global tools (dotnet-ef) ───────────────────────────────────
# Exported here for this process, and appended to the shell rc files so later
# terminals in the same session inherit it too.
TOOLS_DIR="${HOME}/.dotnet/tools"
export PATH="${PATH}:${TOOLS_DIR}"
for rc in "${HOME}/.bashrc" "${HOME}/.profile"; do
  [ -f "$rc" ] || continue
  if ! grep -qF '.dotnet/tools' "$rc"; then
    printf '\n# .NET global tools (dotnet-ef)\nexport PATH="$PATH:%s"\n' "$TOOLS_DIR" >> "$rc"
    log "Added ${TOOLS_DIR} to PATH in $(basename "$rc")"
  fi
done

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

# ── Docker daemon ────────────────────────────────────────────────────────────
# The integration tests and parts of the unit suite start PostgreSQL through
# Testcontainers, which needs a daemon. Cloud containers ship the binaries but
# no init system to run them, so start it here when it is not already up.
if command -v dockerd >/dev/null 2>&1 && ! docker info >/dev/null 2>&1; then
  log "Starting the Docker daemon (Testcontainers needs it)"
  if [ "$(id -u)" -eq 0 ]; then
    nohup dockerd >/var/log/dockerd.log 2>&1 &
  else
    sudo -n true 2>/dev/null && sudo -b nohup dockerd >/var/log/dockerd.log 2>&1 || true
  fi
  for _ in $(seq 1 15); do
    docker info >/dev/null 2>&1 && break
    sleep 1
  done
  if docker info >/dev/null 2>&1; then
    log "  Docker daemon up"
  else
    log "  Could not start dockerd — Testcontainers-backed tests will fail (see /var/log/dockerd.log)"
  fi
fi

# ── Warm the NuGet cache ─────────────────────────────────────────────────────
# CardiTrack.Server.slnf is the whole solution minus CardiTrack.Mobile, which
# needs the maui-android workload and the Android SDK. Set INSTALL_MAUI=1 on
# install-toolchain.sh to add those and restore CardiTrack.sln instead.
if [ -f "$MARKER" ]; then
  log "Packages already restored in this container — skipping"
else
  log "Restoring NuGet packages (first run in this container)"
  if dotnet restore "${REPO_ROOT}/CardiTrack.Server.slnf" --nologo 2>&1 | tail -3; then
    mkdir -p "$(dirname "$MARKER")" && touch "$MARKER"
    log "Restore complete"
  else
    log "Restore failed (NuGet unreachable?) — run 'dotnet restore CardiTrack.Server.slnf' once network is available"
  fi
fi

log "Ready: dotnet $(dotnet --version 2>/dev/null || echo MISSING), terraform $(terraform version 2>/dev/null | head -1 | awk '{print $2}' || echo MISSING)"
