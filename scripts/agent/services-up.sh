#!/usr/bin/env bash
# Bring up what the API, Worker and the test suites need from a container that
# has no init system: Docker (installed if absent), its daemon, then PostgreSQL
# 17 and Redis 7 from the repo's docker-compose.yml. Idempotent; safe as a VM
# "start" command.
#
# Never hard-fails. If Docker cannot be installed or started here, that is
# reported and the script exits 0 — the server projects still build; only the
# Testcontainers-backed tests and a running API/Worker need the daemon.
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO_ROOT"

log() { printf '\033[0;36m[services]\033[0m %s\n' "$*"; }

# Root, or passwordless sudo — needed for the install and for starting dockerd.
if [ "$(id -u)" -eq 0 ]; then
  ROOT=""
elif sudo -n true 2>/dev/null; then
  ROOT="sudo -n"
else
  ROOT=""
  log "not root and no passwordless sudo — cannot install or start Docker from here"
fi

# ── Install Docker when the image ships without it ───────────────────────────
# The documented Cursor/Claude cloud base images have no Docker (AGENTS.md).
# Ubuntu's docker.io package is reachable where Docker's own apt host is not.
# The daemon settings are the ones AGENTS.md records for this kernel: the
# fuse-overlayfs storage driver with the containerd snapshotter off, and the
# legacy iptables backend.
if ! command -v docker >/dev/null 2>&1; then
  if [ -n "$ROOT" ] || [ "$(id -u)" -eq 0 ]; then
    log "Docker is not installed — installing docker.io and fuse-overlayfs from the Ubuntu archive"
    if $ROOT env DEBIAN_FRONTEND=noninteractive apt-get update -qq >/dev/null 2>&1 && \
       $ROOT env DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends docker.io docker-compose-v2 fuse-overlayfs >/dev/null 2>&1; then
      log "  Docker installed"
    else
      log "  Docker install failed (archive unreachable?) — Postgres/Redis not started. See AGENTS.md."
      exit 0
    fi
  else
    log "Docker is not installed and there is no way to install it from here — Postgres/Redis not started."
    exit 0
  fi
fi

# ── Daemon configuration ─────────────────────────────────────────────────────
# Applied whether Docker was just installed or came with the image, before the
# daemon is started: without it dockerd will not come up on this kernel.
if [ -n "$ROOT" ] || [ "$(id -u)" -eq 0 ]; then
  if [ ! -f /etc/docker/daemon.json ] && ! docker info >/dev/null 2>&1; then
    $ROOT mkdir -p /etc/docker
    printf '%s\n' '{"storage-driver":"fuse-overlayfs","features":{"containerd-snapshotter":false}}' \
      | $ROOT tee /etc/docker/daemon.json >/dev/null
    $ROOT update-alternatives --set iptables /usr/sbin/iptables-legacy >/dev/null 2>&1 || true
    $ROOT update-alternatives --set ip6tables /usr/sbin/ip6tables-legacy >/dev/null 2>&1 || true
    log "Wrote /etc/docker/daemon.json (fuse-overlayfs, snapshotter off) and selected legacy iptables"
  fi
fi

# ── Daemon ───────────────────────────────────────────────────────────────────
if ! docker info >/dev/null 2>&1; then
  log "Starting the Docker daemon"
  # The redirection has to happen inside the privileged shell: an unprivileged
  # one cannot open /var/log/dockerd.log, and the daemon would never start.
  if [ "$(id -u)" -eq 0 ]; then
    sh -c 'nohup dockerd >/var/log/dockerd.log 2>&1 &'
  elif [ -n "$ROOT" ]; then
    $ROOT sh -c 'nohup dockerd >/var/log/dockerd.log 2>&1 &'
  fi
  for _ in $(seq 1 20); do
    { docker info >/dev/null 2>&1 || { [ -n "$ROOT" ] && $ROOT docker info >/dev/null 2>&1; }; } && break
    sleep 1
  done
fi

# Socket access for the non-root agent: membership of the docker group, which
# new terminals pick up. This shell may not have it yet, so the compose calls
# below fall back to sudo. Never chmod 666 the socket — that hands every
# process on the VM root through Docker.
DOCKER="docker"
if ! docker info >/dev/null 2>&1; then
  if [ -n "$ROOT" ] && $ROOT docker info >/dev/null 2>&1; then
    DOCKER="$ROOT docker"
    if getent group docker >/dev/null 2>&1 && ! id -nG | tr ' ' '\n' | grep -qx docker; then
      $ROOT usermod -aG docker "$(id -un)" 2>/dev/null && log "Added $(id -un) to the docker group (takes effect in new terminals)"
    fi
  else
    log "Docker daemon did not come up (see /var/log/dockerd.log) — Postgres/Redis not started."
    exit 0
  fi
fi

# ── Postgres + Redis ─────────────────────────────────────────────────────────
log "Starting db and redis"
$DOCKER compose up -d db redis >/dev/null 2>&1 || $DOCKER compose up -d db redis

ready() {
  $DOCKER compose exec -T db pg_isready -U postgres -d carditrack >/dev/null 2>&1 && \
  [ "$($DOCKER compose exec -T redis redis-cli ping 2>/dev/null | tr -d '\r')" = "PONG" ]
}
for _ in $(seq 1 30); do
  ready && break
  sleep 2
done
if ready; then
  log "Postgres + Redis up."
else
  log "Postgres or Redis did not report ready in 60s — check 'docker compose logs db redis'."
fi
exit 0
