#!/usr/bin/env bash
# Bring up what the API, Worker and the test suites need from a container that
# has no init system: the Docker daemon, then PostgreSQL 17 and Redis 7 from the
# repo's docker-compose.yml. Idempotent; safe as a VM "start" command.
#
# Never hard-fails. A missing Docker daemon is reported, not fatal — the server
# projects still build, and only the Testcontainers-backed tests and a running
# API/Worker need it.
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO_ROOT"

log() { printf '\033[0;36m[services]\033[0m %s\n' "$*"; }

if ! command -v docker >/dev/null 2>&1; then
  log "docker is not installed — Postgres/Redis not started. See AGENTS.md (Postgres + Redis need a running Docker daemon) for the install notes on this image."
  exit 0
fi

if ! docker info >/dev/null 2>&1; then
  log "Starting the Docker daemon"
  if [ "$(id -u)" -eq 0 ]; then
    nohup dockerd >/var/log/dockerd.log 2>&1 &
  else
    sudo -n true 2>/dev/null && sudo -b nohup dockerd >/var/log/dockerd.log 2>&1
  fi
  for _ in $(seq 1 20); do
    docker info >/dev/null 2>&1 && break
    sleep 1
  done
fi

if ! docker info >/dev/null 2>&1; then
  log "Docker daemon did not come up (see /var/log/dockerd.log) — Postgres/Redis not started."
  exit 0
fi

# Socket permissions for the non-root agent user, when the daemon is root's.
if [ ! -w /var/run/docker.sock ] && [ "$(id -u)" -ne 0 ]; then
  sudo -n chmod 666 /var/run/docker.sock 2>/dev/null || true
fi

log "Starting db and redis"
docker compose up -d db redis >/dev/null 2>&1 || docker compose up -d db redis

for _ in $(seq 1 30); do
  docker compose exec -T db pg_isready -U postgres -d carditrack >/dev/null 2>&1 && break
  sleep 2
done
if docker compose exec -T db pg_isready -U postgres -d carditrack >/dev/null 2>&1; then
  log "Postgres + Redis up."
else
  log "Postgres did not report ready in 60s — check 'docker compose logs db'."
fi
exit 0
