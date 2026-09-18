#!/usr/bin/env bash
# Run every test suite the repository has, and say plainly which ones it does not.
#
#   - CardiTrack.UnitTests and CardiTrack.IntegrationTests, Release, after one
#     build of the server filter. Both start PostgreSQL through Testcontainers, so
#     a Docker daemon must be reachable (scripts/agent/services-up.sh starts one).
#   - CardiTrack.E2ETests is an empty scaffold and CardiTrack.Mobile has no test
#     project. Neither is run, and neither is reported as passing.
#
# TESTCONTAINERS_RYUK_DISABLED=true matches CI: the Ryuk reaper container cannot
# run in most agent sandboxes, and the suites clean up after themselves.
#
# Usage: scripts/agent/test-all.sh [--unit | --integration]
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO_ROOT"

RUN_UNIT=1; RUN_INTEGRATION=1
case "${1:-}" in
  "") ;;
  --unit) RUN_INTEGRATION=0 ;;
  --integration) RUN_UNIT=0 ;;
  *) echo "usage: $0 [--unit | --integration]" >&2; exit 2 ;;
esac

log() { printf '\033[0;36m[test-all]\033[0m %s\n' "$*"; }
export TESTCONTAINERS_RYUK_DISABLED="${TESTCONTAINERS_RYUK_DISABLED:-true}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

# services-up.sh adds a non-root user to the docker group, but a group joined
# after this shell started is not in its credentials yet. `sg docker` opens a
# shell that has it — re-run under that once, so Testcontainers inherits access.
if ! docker info >/dev/null 2>&1 && [ -z "${TEST_ALL_SG:-}" ] && command -v sg >/dev/null 2>&1 \
   && getent group docker | awk -F: '{print $4}' | tr ',' '\n' | grep -qx "$(id -un)"; then
  export TEST_ALL_SG=1
  exec sg docker -c "bash '$0' $*"
fi
if ! docker info >/dev/null 2>&1; then
  log "No Docker daemon reachable — Testcontainers-backed tests will fail at fixture setup. Run scripts/agent/services-up.sh first."
fi

log "Building CardiTrack.Server.slnf (Release) once for both suites"
if ! dotnet build CardiTrack.Server.slnf -c Release --nologo 2>&1 | tail -5; then
  log "Build failed — nothing was tested."
  exit 1
fi

RESULTS=()
FAILED=0
run_suite() {
  local name="$1" proj="$2"
  log "Running $name"
  local out
  if out=$(dotnet test "$proj" -c Release --no-build --nologo 2>&1); then
    RESULTS+=("$name: $(printf '%s\n' "$out" | grep -E 'Passed!|Failed!|Total tests' | tail -1 | sed 's/^ *//')")
  else
    printf '%s\n' "$out" | grep -E 'Failed |error|Failed!' | head -40
    RESULTS+=("$name: FAILED — $(printf '%s\n' "$out" | grep -E 'Failed!|Passed!' | tail -1 | sed 's/^ *//')")
    FAILED=1
  fi
}

[ "$RUN_UNIT" -eq 1 ]        && run_suite "unit"        tests/CardiTrack.UnitTests/CardiTrack.UnitTests.csproj
[ "$RUN_INTEGRATION" -eq 1 ] && run_suite "integration" tests/CardiTrack.IntegrationTests/CardiTrack.IntegrationTests.csproj

echo
log "Summary"
for r in "${RESULTS[@]}"; do printf '  %s\n' "$r"; done
printf '  %s\n' "e2e: NOT RUN — tests/CardiTrack.E2ETests contains no tests"
printf '  %s\n' "mobile: NOT RUN — CardiTrack.Mobile has no test project; build it with scripts/agent/build-all.sh"
exit $FAILED
