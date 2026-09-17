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

# ── Docker, Postgres, Redis ──────────────────────────────────────────────────
# The integration tests and parts of the unit suite start PostgreSQL through
# Testcontainers, which needs a daemon; the API and Worker need Postgres and
# Redis. scripts/agent/services-up.sh is the one place that installs Docker
# when the image has none, configures and starts the daemon, sorts out socket
# access for a non-root session and brings the two services up — the same path
# Cursor's environment `start` takes. Linux only: on the Windows dev box Docker
# Desktop is already running and nothing here applies.
if [ "$(uname -s)" = "Linux" ] && [ -x "${REPO_ROOT}/scripts/agent/services-up.sh" ]; then
  "${REPO_ROOT}/scripts/agent/services-up.sh"
elif command -v dockerd >/dev/null 2>&1 && ! docker info >/dev/null 2>&1; then
  log "Starting the Docker daemon (Testcontainers needs it)"
  if [ "$(id -u)" -eq 0 ]; then
    nohup dockerd >/var/log/dockerd.log 2>&1 &
  else
    # The redirection has to happen inside the privileged shell: an unprivileged
    # one cannot open /var/log/dockerd.log, so the daemon would never start.
    sudo -n sh -c 'nohup dockerd >/var/log/dockerd.log 2>&1 &' 2>/dev/null || true
  fi
  for _ in $(seq 1 15); do
    { docker info >/dev/null 2>&1 || sudo -n docker info >/dev/null 2>&1; } && break
    sleep 1
  done
  if docker info >/dev/null 2>&1; then
    log "  Docker daemon up"
  elif sudo -n docker info >/dev/null 2>&1; then
    # Root's daemon, non-root session: grant the docker group rather than
    # loosening the socket (a world-writable socket is root for every process).
    # Group membership lands in new shells, so this one still needs sudo.
    if getent group docker >/dev/null 2>&1 && ! id -nG | tr ' ' '\n' | grep -qx docker; then
      sudo -n usermod -aG docker "$(id -un)" 2>/dev/null || true
    fi
    log "  Docker daemon up (root); $(id -un) added to the docker group — open a new terminal before running the tests, or prefix docker with sudo in this one"
  else
    log "  Could not start dockerd — Testcontainers-backed tests will fail (see /var/log/dockerd.log)"
  fi
fi

# ── Warm the NuGet cache ─────────────────────────────────────────────────────
# CardiTrack.Server.slnf is the whole solution minus CardiTrack.Mobile, which
# needs the maui-android workload and the Android SDK. With INSTALL_MAUI=1 the
# toolchain step above installed those, so restore CardiTrack.sln instead —
# under its own marker, so a container first bootstrapped without mobile
# restores again when the variable is later switched on.
if [ "${INSTALL_MAUI:-0}" = "1" ]; then
  SOLUTION="${REPO_ROOT}/CardiTrack.sln"
  MARKER="${MARKER}-sln"
else
  SOLUTION="${REPO_ROOT}/CardiTrack.Server.slnf"
fi
if [ -f "$MARKER" ]; then
  log "Packages already restored in this container — skipping"
else
  log "Restoring NuGet packages for $(basename "$SOLUTION") (first run in this container)"
  if dotnet restore "$SOLUTION" --nologo 2>&1 | tail -3; then
    mkdir -p "$(dirname "$MARKER")" && touch "$MARKER"
    log "Restore complete"
  else
    log "Restore failed (NuGet unreachable?) — run 'dotnet restore CardiTrack.Server.slnf' once network is available"
  fi
fi

log "Ready: dotnet $(dotnet --version 2>/dev/null || echo MISSING), terraform $(terraform version 2>/dev/null | head -1 | awk '{print $2}' || echo MISSING)"
if [ "${INSTALL_MAUI:-0}" = "1" ]; then
  log "Mobile: maui-android $(dotnet workload list 2>/dev/null | grep -q '^maui-android' && echo present || echo MISSING); Android SDK $([ -d "${ANDROID_SDK_ROOT_DIR:-$HOME/Android/Sdk}/platforms" ] && echo present || echo MISSING)"
fi
