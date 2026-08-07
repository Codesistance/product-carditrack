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

DOCKERD_LOG="${DOCKERD_LOG:-/var/log/dockerd.log}"

log() { printf '\033[0;36m[bootstrap]\033[0m %s\n' "$*"; }

# Version of an installed tool, or "MISSING" when it is absent or silent.
# See install-toolchain.sh for why this avoids `| head -1` under pipefail.
tool_version() {
  local out
  out="$("$@" 2>/dev/null | awk 'NR==1{print $NF; exit}')" || true
  printf '%s' "${out:-MISSING}"
}

as_root() {
  if [ "$(id -u)" -eq 0 ]; then
    "$@"
  elif command -v sudo >/dev/null 2>&1 && sudo -n true 2>/dev/null; then
    sudo "$@"
  else
    return 1
  fi
}

# Start a daemon fully detached: setsid gives it its own session, so it survives
# this script's process group being torn down when the session hook finishes.
# The redirection runs inside the elevated shell so the log path needs no
# write access from the calling user.
as_root_bg() {
  as_root sh -c "exec setsid nohup $* >\"${DOCKERD_LOG}\" 2>&1 </dev/null &"
}

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
start_dockerd() {
  # A daemon killed without an init system to reap it leaves its socket and its
  # managed-containerd pid file behind. dockerd then reads that pid file, decides
  # containerd "is still running", waits 15s for a process that is gone, and
  # exits. Clear the leftovers whenever no dockerd is actually running.
  if ! pgrep -x dockerd >/dev/null 2>&1; then
    as_root rm -f /var/run/docker.sock /var/run/docker/containerd/containerd.pid 2>/dev/null || true
  fi

  # No root and no passwordless sudo — nothing to wait for.
  as_root_bg dockerd || return 1

  # dockerd's own wait for containerd is 15s, so allow comfortably more than
  # that before calling it a failure.
  for _ in $(seq 1 45); do
    docker info >/dev/null 2>&1 && return 0
    sleep 1
  done
  return 1
}

if command -v dockerd >/dev/null 2>&1 && ! docker info >/dev/null 2>&1; then
  log "Starting the Docker daemon (Testcontainers needs it)"
  if start_dockerd; then
    log "  Docker daemon up"
  else
    log "  Could not start dockerd — Testcontainers-backed tests will fail (see ${DOCKERD_LOG})"
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

log "Ready: dotnet $(tool_version dotnet --version), terraform $(tool_version terraform version)"
